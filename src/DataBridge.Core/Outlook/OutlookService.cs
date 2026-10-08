// Moteur Outlook repris de Sauvegarde Outlook (même format de PST et mêmes propriétés de dossier :
// une sauvegarde faite par DataBridge peut aussi être restaurée avec Sauvegarde Outlook, et inversement).
#nullable disable
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace DataBridge.Core.Outlook;

/// <summary>
/// Accès à Outlook classique par COM (liaison tardive, aucune dépendance aux assemblies Office).
/// Toutes les méthodes doivent être appelées depuis le thread STA de <see cref="StaWorker"/>.
///
/// Règles de sécurité :
///  - une boîte lue (source de sauvegarde, PST de restauration) n'est jamais modifiée : jamais de Save,
///    Move, Delete, Copy ou Send sur ses éléments ; chaque élément est exporté en .msg temporaire,
///    rouvert hors de toute boîte, et c'est cette copie qui est déplacée vers la destination ;
///  - le PST de sauvegarde n'est jamais ouvert directement pour une restauration : une copie de travail est utilisée ;
///  - la restauration ne fait qu'ajouter : rien n'est supprimé ni modifié dans la boîte cible
///    (seule exception, sur demande explicite : le remplacement du cache d'autocomplétion, après copie de l'ancien) ;
///  - aucun fichier existant n'est jamais écrasé ; aucun mail ni invitation n'est jamais envoyé.
/// </summary>
public sealed class OutlookService
{
    const string PR_ATTR_HIDDEN = "http://schemas.microsoft.com/mapi/proptag/0x10F4000B";
    const string PR_HASATTACH = "http://schemas.microsoft.com/mapi/proptag/0x0E1B000B";
    const string PR_ROAMING_BINARYSTREAM = "http://schemas.microsoft.com/mapi/proptag/0x7C090102";
    const string PidLidRecurring = "http://schemas.microsoft.com/mapi/id/{00062002-0000-0000-C000-000000000046}/00008223000B";
    const string PropSpecial = "http://schemas.microsoft.com/mapi/string/{6A1C1E52-3B8F-4C1D-9E5A-2F7B4D8C9A10}/SauvegardeOutlook.DossierSpecial";
    const string PropSource = "http://schemas.microsoft.com/mapi/string/{6A1C1E52-3B8F-4C1D-9E5A-2F7B4D8C9A10}/SauvegardeOutlook.Source";
    const string AutocompleteClass = "IPM.Configuration.Autocomplete";
    const string AutoContactsFolder = "Adresses autocomplétion";

    const int olMailItem = 0, olAppointmentItem = 1, olContactItem = 2;
    const int olFolderInbox = 6, olFolderCalendar = 9, olFolderContacts = 10;
    const int olMSGUnicode = 9, olStoreUnicode = 2, olCreateAppointment = 1;
    const int olUserItems = 0, olHiddenItems = 1, olIdentifyByMessageClass = 2;
    const int olExchangePublicFolder = 2, olNotExchange = 3;
    const int MAPI_E_NOT_FOUND = unchecked((int)0x8004010F);

    static readonly string[] MailCols = { "EntryID", "Subject", "SenderName", "ReceivedTime", "CreationTime", PR_HASATTACH };
    static readonly string[] ContactCols = { "EntryID", "FullName", "Email1Address", "CompanyName", "MessageClass", "Subject" };
    static readonly string[] EventCols = { "EntryID", "Subject", "Start", "End", "IsRecurring", "Location", PidLidRecurring };
    static readonly string[] EmailCols = { "EntryID", "Email1Address", "Email2Address", "Email3Address" };

    dynamic _app;
    dynamic _ns;

    // Sauvegarde ouverte pour restauration (copie de travail).
    string _backupStoreId;
    string _backupCopyPath;     // null si le fichier d'origine a été ouvert directement
    bool _backupWasAttached;    // déjà ouvert dans Outlook avant nous : on ne le retire pas

    // ================================================================== connexion

    void Connect()
    {
        if (_ns != null)
        {
            try { _ = (int)_ns.Stores.Count; return; }
            catch { Disconnect(); }
        }

        if (Process.GetProcessesByName("OUTLOOK").Length == 0)
            throw new UserMessageException(
                "Outlook classique n'est pas ouvert.\n\nOuvrez Outlook, attendez qu'il soit complètement chargé, puis réessayez.");

        Type t = Type.GetTypeFromProgID("Outlook.Application");
        if (t == null)
            throw new UserMessageException(
                "Outlook classique n'est pas installé sur ce PC.\n(Le « nouvel Outlook » ne permet pas ce type d'opération.)");

        try
        {
            _app = Activator.CreateInstance(t);
            _ns = _app.GetNamespace("MAPI");
        }
        catch (Exception ex)
        {
            Disconnect();
            throw new UserMessageException(
                "Impossible de se connecter à Outlook : " + ex.Message +
                "\n\nVérifiez qu'Outlook et cette application sont lancés avec les mêmes droits (ni l'un ni l'autre « en tant qu'administrateur »).");
        }
    }

    /// <summary>Libère la connexion. Ne ferme jamais Outlook.</summary>
    public void Disconnect()
    {
        Release(_ns);
        Release(_app);
        _ns = null;
        _app = null;
    }

    // ================================================================== liste des boîtes

    public List<StoreEntry> ListStores()
    {
        Connect();

        var accountsByStore = new Dictionary<string, List<string>>();
        dynamic accounts = _ns.Accounts;
        int na = accounts.Count;
        for (int i = 1; i <= na; i++)
        {
            dynamic acc = accounts.Item(i);
            try
            {
                string type = AccountTypeName(Int(() => acc.AccountType, -1));
                string smtp = Str(() => acc.SmtpAddress);
                string label = type + " " + (smtp != "" ? smtp : Str(() => acc.DisplayName));
                dynamic ds = acc.DeliveryStore;
                string sid = ds.StoreID;
                Release(ds);
                if (!accountsByStore.TryGetValue(sid, out var l)) accountsByStore[sid] = l = new List<string>();
                l.Add(label);
            }
            catch { /* compte sans boîte de remise */ }
            finally { Release(acc); }
        }
        Release(accounts);

        string defaultId = Str(() => _ns.DefaultStore.StoreID);
        var list = new List<StoreEntry>();
        dynamic stores = _ns.Stores;
        int n = stores.Count;
        for (int i = 1; i <= n; i++)
        {
            dynamic s = stores.Item(i);
            try
            {
                int xt = Int(() => s.ExchangeStoreType, olNotExchange);
                if (xt == olExchangePublicFolder) continue; // dossiers publics : hors sujet
                string id = s.StoreID;
                if (id == _backupStoreId) continue;         // copie de travail d'une sauvegarde
                var e = new StoreEntry
                {
                    StoreId = id,
                    DisplayName = Str(() => s.DisplayName),
                    FilePath = Str(() => s.FilePath),
                    IsDefault = id == defaultId,
                };
                string kind = accountsByStore.TryGetValue(id, out var accs)
                    ? "compte " + string.Join(", ", accs)
                    : xt == olNotExchange ? "fichier de données" : "boîte Exchange / Microsoft 365";
                e.Description = $"{e.DisplayName}   —   {kind}"
                                + (e.FilePath != "" ? $"   —   {e.FilePath}" : "")
                                + (e.IsDefault ? "   [boîte par défaut du profil]" : "");
                list.Add(e);
            }
            catch { }
            finally { Release(s); }
        }
        Release(stores);
        return list;
    }

    // ================================================================== lecture d'une boîte

    public StoreContent LoadStore(string storeId, IProgress<string> progress, CancellationToken ct)
    {
        Connect();
        dynamic store = FindStore(storeId);
        if (store == null) throw new UserMessageException("Boîte introuvable. Relancez l'analyse d'Outlook.");

        var c = new StoreContent();
        try
        {
            var defaults = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (int t in SpecialFolders.All)
            {
                string id = DefaultFolderId((object)store, t);
                if (id != null) defaults.TryAdd(id, t);
            }
            string outboxId = DefaultFolderId((object)store, SpecialFolders.Outbox);

            dynamic root = store.GetRootFolder();
            try
            {
                c.BackupInfo = GetTag((object)root, PropSource) ?? "";
                Walk((object)root, new List<string>(), new List<int>(), null, c, storeId, outboxId, defaults, progress, ct);
            }
            finally { Release(root); }

            progress.Report("Recherche du cache d'autocomplétion…");
            c.Autocomplete = ProbeAutocomplete((object)store);
        }
        finally { Release(store); }
        return c;
    }

    void Walk(object folderObj, List<string> path, List<int> pathSpecial, FolderNode mailParent, StoreContent c, string storeId,
              string outboxId, Dictionary<string, int> defaults, IProgress<string> progress, CancellationToken ct)
    {
        dynamic folder = folderObj;
        dynamic folders = folder.Folders;
        int n = folders.Count;
        for (int i = 1; i <= n; i++)
        {
            ct.ThrowIfCancellationRequested();
            dynamic f = folders.Item(i);
            string pathText = string.Join(" \\ ", path);
            try
            {
                string name = f.Name;
                var p = new List<string>(path) { name };
                pathText = string.Join(" \\ ", p);
                if (IsHidden((object)f)) continue;

                string entryId = f.EntryID;
                int type = Int(() => f.DefaultItemType, -1);

                if (outboxId != null && string.Equals(entryId, outboxId, StringComparison.OrdinalIgnoreCase))
                {
                    c.Warnings.Add($"Dossier « {pathText} » ignoré : il contient des messages en attente d'envoi, il n'est pas touché par sécurité.");
                    continue;
                }

                int sp = int.TryParse(GetTag((object)f, PropSpecial), out int tagged) ? tagged : 0;
                if (sp == 0 && defaults.TryGetValue(entryId, out int d)) sp = d;
                if (sp == 0 && path.Count == 0) sp = SpecialFolders.Guess(name, type);
                var ps = new List<int>(pathSpecial) { sp };

                FolderNode mailNode = null;
                if (type == olMailItem)
                {
                    progress.Report("Lecture des mails : " + pathText);
                    mailNode = new FolderNode { EntryId = entryId, Name = name, Path = p, PathSpecial = ps, SpecialType = sp, Parent = mailParent };
                    foreach (var r in ReadRows((object)f, MailCols))
                    {
                        string id = S(r[0]);
                        if (id == "") continue;
                        mailNode.Mails.Add(new MailEntry
                        {
                            EntryId = id, Subject = S(r[1]), Sender = S(r[2]),
                            Date = D(r[3]) ?? D(r[4]), HasAttachments = B(r[5]), Folder = mailNode,
                        });
                    }
                    (mailParent?.Children ?? c.MailRoots).Add(mailNode);
                }
                else if (type == olContactItem)
                {
                    progress.Report("Lecture des contacts : " + pathText);
                    var node = new FolderNode { EntryId = entryId, Name = name, Path = p, PathSpecial = ps, SpecialType = sp };
                    c.ContactFolders.Add(node);
                    foreach (var r in ReadRows((object)f, ContactCols))
                    {
                        string id = S(r[0]);
                        if (id == "") continue;
                        c.Contacts.Add(new ContactEntry
                        {
                            EntryId = id, Name = ContactName(r[1], r[5]), Email = S(r[2]), Company = S(r[3]),
                            IsList = S(r[4]).StartsWith("IPM.DistList", StringComparison.OrdinalIgnoreCase), Folder = node,
                        });
                    }
                }
                else if (type == olAppointmentItem)
                {
                    progress.Report("Lecture du calendrier : " + pathText);
                    var node = new FolderNode { EntryId = entryId, Name = name, Path = p, PathSpecial = ps, SpecialType = sp };
                    c.CalendarFolders.Add(node);
                    foreach (var r in ReadRows((object)f, EventCols))
                    {
                        string id = S(r[0]);
                        if (id == "") continue;
                        var e = new EventEntry
                        {
                            EntryId = id, Subject = S(r[1]), Start = D(r[2]) ?? DateTime.MinValue, End = D(r[3]) ?? DateTime.MinValue,
                            IsRecurring = B(r[4]) || B(r[6]), Location = S(r[5]), Folder = node,
                        };
                        if (e.IsRecurring) FillRecurrence(e, storeId);
                        c.Events.Add(e);
                    }
                }

                // Un dossier de mails sous un dossier non-mail devient une racine de l'arborescence.
                Walk((object)f, p, ps, mailNode, c, storeId, outboxId, defaults, progress, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { c.Warnings.Add($"Dossier « {pathText} » non lu : {ex.Message}"); }
            finally { Release(f); }
        }
        Release(folders);
    }

    void FillRecurrence(EventEntry e, string storeId)
    {
        dynamic item = null, rp = null;
        try
        {
            item = _ns.GetItemFromID(e.EntryId, storeId);
            rp = item.GetRecurrencePattern(); // lecture seule, rien n'est enregistré
            e.SeriesStart = D((object)rp.PatternStartDate);
            e.NoEnd = (bool)rp.NoEndDate;
            e.SeriesEnd = e.NoEnd ? null : D((object)rp.PatternEndDate);
            e.Recurrence = RecurrenceText((int)rp.RecurrenceType);
        }
        catch { e.Recurrence = "Récurrente"; }
        finally { Release(rp); Release(item); }
    }

    // ================================================================== lecture tabulaire

    /// <summary>Lit les colonnes demandées (Table Outlook rapide, ou parcours des éléments en secours).</summary>
    static List<object[]> ReadRows(object folder, string[] cols)
    {
        try { return ReadRowsTable(folder, cols); }
        catch { return ReadRowsItems(folder, cols); }
    }

    static List<object[]> ReadRowsTable(object folderObj, string[] cols)
    {
        dynamic folder = folderObj;
        var rows = new List<object[]>();
        dynamic table = folder.GetTable(Type.Missing, olUserItems);
        dynamic columns = table.Columns;
        try
        {
            columns.RemoveAll();
            var added = new List<int>();
            for (int j = 0; j < cols.Length; j++)
            {
                try { columns.Add(cols[j]); added.Add(j); } catch { /* colonne non prise en charge */ }
            }
            if (added.Count == 0 || added[0] != 0) throw new InvalidOperationException("EntryID indisponible.");

            while (!(bool)table.EndOfTable)
            {
                dynamic row = table.GetNextRow();
                try
                {
                    object[] v = ToArray((object)row.GetValues());
                    var full = new object[cols.Length];
                    for (int k = 0; k < added.Count && k < v.Length; k++) full[added[k]] = v[k];
                    rows.Add(full);
                }
                finally { Release(row); }
            }
        }
        finally { Release(columns); Release(table); }
        return rows;
    }

    static List<object[]> ReadRowsItems(object folderObj, string[] cols)
    {
        dynamic folder = folderObj;
        var rows = new List<object[]>();
        dynamic items = folder.Items;
        int n = items.Count;
        for (int i = 1; i <= n; i++)
        {
            object it = null;
            try
            {
                it = items.Item(i);
                var v = new object[cols.Length];
                for (int j = 0; j < cols.Length; j++) v[j] = GetProp(it, cols[j]);
                rows.Add(v);
            }
            catch { }
            finally { Release(it); }
        }
        Release(items);
        return rows;
    }

    static object GetProp(object item, string name)
    {
        try
        {
            if (name.StartsWith("http://", StringComparison.Ordinal))
            {
                dynamic pa = ((dynamic)item).PropertyAccessor;
                try { return pa.GetProperty(name); }
                finally { Release(pa); }
            }
            return item.GetType().InvokeMember(name, BindingFlags.GetProperty, null, item, null);
        }
        catch { return null; }
    }

    // ================================================================== autocomplétion (lecture)

    static AutocompleteInfo ProbeAutocomplete(object store)
    {
        var info = new AutocompleteInfo();
        var (data, why) = ReadAutocomplete(store);
        if (data == null) { info.Message = why; return info; }
        info.Found = true;
        info.Size = data.Length;
        info.Entries = AutocompleteParser.CountRows(data);
        return info;
    }

    /// <summary>
    /// Lit le flux d'autocomplétion de la boîte de réception d'une boîte. L'existence est vérifiée
    /// d'abord via une table d'éléments cachés, pour ne jamais laisser GetStorage créer un élément.
    /// </summary>
    static (byte[] Data, string Why) ReadAutocomplete(object storeObj)
    {
        dynamic store = storeObj;
        dynamic inbox = null, table = null, storage = null, pa = null;
        try
        {
            try { inbox = store.GetDefaultFolder(olFolderInbox); }
            catch { return (null, "cette boîte n'a pas de « Boîte de réception » reconnue par Outlook."); }

            try
            {
                table = inbox.GetTable($"[MessageClass] = '{AutocompleteClass}'", olHiddenItems);
                if ((bool)table.EndOfTable)
                    return (null, "aucun cache d'autocomplétion n'est stocké dans cette boîte (Outlook le place dans le fichier de données par défaut du profil).");
            }
            catch (Exception ex)
            {
                return (null, "les éléments cachés de cette boîte ne sont pas accessibles (" + ex.Message + ").");
            }

            try
            {
                storage = inbox.GetStorage(AutocompleteClass, olIdentifyByMessageClass);
                pa = storage.PropertyAccessor;
                object v = pa.GetProperty(PR_ROAMING_BINARYSTREAM);
                if (v is byte[] b && b.Length > 0) return (b, null);
                return (null, "le cache existe mais il est vide.");
            }
            catch (Exception ex)
            {
                return (null, "le cache existe mais n'a pas pu être lu (" + ex.Message + ").");
            }
        }
        finally { Release(pa); Release(storage); Release(table); Release(inbox); }
    }

    // ================================================================== SAUVEGARDE

    public OperationResult Export(ExportRequest req, IProgress<ProgressInfo> progress, CancellationToken ct)
    {
        Connect();
        var res = new OperationResult { Kind = OperationKind.Backup, SourceName = req.StoreName, AutocompleteRequested = req.IncludeAutocomplete };
        res.Mails.Requested = req.MailJobs.Sum(j => j.Mails.Count);
        res.Contacts.Requested = req.Contacts.Count;
        res.Events.Requested = req.Events.Count;

        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        int total = res.Mails.Requested + res.Contacts.Requested + res.Events.Requested + (req.IncludeAutocomplete ? 1 : 0);
        var ctx = new CopyContext { SourceStoreId = req.StoreId, Res = res, Ct = ct, Total = total, Progress = progress };
        bool needPst = req.MailJobs.Count > 0 || req.Contacts.Count > 0 || req.Events.Count > 0;
        dynamic pstStore = null, pstRoot = null;

        try
        {
            if (needPst)
            {
                progress.Report(new ProgressInfo(0, total, "Création du nouveau fichier PST…"));
                string pst = UniquePath(req.DestDir, "Sauvegarde_Outlook_" + stamp, ".pst");
                if (File.Exists(pst)) throw new UserMessageException("Un fichier du même nom existe déjà : " + pst);
                _ns.AddStoreEx(pst, olStoreUnicode);
                res.PstPath = pst;
                pstStore = FindStoreByPath(pst);
                if (pstStore == null) throw new UserMessageException("Le fichier PST n'a pas pu être ouvert par Outlook après sa création.");
                pstRoot = pstStore.GetRootFolder();
                try { pstRoot.Name = "Sauvegarde Outlook " + DateTime.Now.ToString("dd.MM.yyyy HH'h'mm"); } catch { }
                SetTag((object)pstRoot, PropSource, $"{req.StoreName}   (sauvegarde du {DateTime.Now:dd.MM.yyyy HH:mm})");

                object rootObj = pstRoot;
                ctx.Resolve = (f, type) => GetOrCreatePath(rootObj, f.Path, f.PathSpecial, type, ctx.Folders, tag: true);
                Directory.CreateDirectory(ctx.TempDir);
                RunCopies(ctx, req.MailJobs, req.Contacts, req.Events);
            }

            if (req.IncludeAutocomplete)
            {
                ct.ThrowIfCancellationRequested();
                progress.Report(new ProgressInfo(ctx.Done, total, "Sauvegarde de l'autocomplétion…"));
                SaveAutocomplete(req, stamp, res);
                ctx.Step("Autocomplétion");
            }
        }
        catch (OperationCanceledException) { res.Cancelled = true; }
        catch (Exception ex) { res.Fatal = ex.Message; }
        finally
        {
            ctx.ReleaseAll();
            if (pstRoot != null && req.DetachPst)
            {
                try
                {
                    _ns.RemoveStore(pstRoot);
                    res.Detached = true;
                    res.Notes.Add("Outlook peut garder ce PST verrouillé jusqu'à son redémarrage : pour une restauration sur copie de travail, redémarrez d'abord Outlook.");
                }
                catch (Exception ex) { res.Notes.Add("Le PST n'a pas pu être retiré d'Outlook (" + ex.Message + ") : vous pouvez le fermer à la main (clic droit > Fermer)."); }
            }
            Release(pstRoot);
            Release(pstStore);
        }

        res.NotProcessed = Math.Max(0, total - ctx.Done);
        res.ReportPath = WriteReport(req.DestDir, "Compte-rendu_sauvegarde_" + stamp, res);
        return res;
    }

    void SaveAutocomplete(ExportRequest req, string stamp, OperationResult res)
    {
        dynamic store = FindStore(req.StoreId);
        if (store == null) { res.AutocompleteLines.Add("NON sauvegardée : boîte source introuvable."); return; }
        try
        {
            var (data, why) = ReadAutocomplete((object)store);
            if (data == null) { res.AutocompleteLines.Add("NON sauvegardée : " + why); return; }

            string bin = UniquePath(req.DestDir, "Autocompletion_" + stamp, ".bin");
            WriteNew(bin, data);
            if (new FileInfo(bin).Length != data.Length)
            {
                res.AutocompleteLines.Add("NON sauvegardée : le fichier écrit est incomplet.");
                return;
            }
            res.AutocompleteFiles.Add(bin);

            string detail;
            try
            {
                var entries = AutocompleteParser.Parse(data, out bool complete);
                int announced = AutocompleteParser.CountRows(data) ?? entries.Count;
                var sb = new StringBuilder("Nom;Adresse e-mail\r\n");
                foreach (var e in entries) sb.Append(Csv(e.Name)).Append(';').Append(Csv(e.Email)).Append("\r\n");
                string csv = UniquePath(req.DestDir, "Autocompletion_" + stamp, ".csv");
                WriteNew(csv, Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray());
                res.AutocompleteFiles.Add(csv);
                detail = complete
                    ? $"{entries.Count} entrée(s) décodée(s), liste lisible en CSV"
                    : $"liste CSV PARTIELLE : {entries.Count} entrée(s) décodée(s) sur {announced} (la copie brute, elle, est complète)";
            }
            catch
            {
                detail = "copie brute uniquement : le contenu n'a pas pu être décodé, pas de liste CSV";
            }
            res.AutocompleteOk = true;
            res.AutocompleteLines.Add($"sauvegardée à part ({data.Length:N0} octets ; {detail}).");
        }
        catch (Exception ex) { res.AutocompleteLines.Add("NON sauvegardée : " + ex.Message); }
        finally { Release(store); }
    }

    // ================================================================== RESTAURATION : ouverture de la sauvegarde

    /// <summary>
    /// Ouvre une COPIE de travail du PST de sauvegarde (Outlook modifie toujours un peu les PST qu'il ouvre).
    /// Si Outlook verrouille encore le fichier (cas normal juste après une sauvegarde, jusqu'à son redémarrage),
    /// lève <see cref="BackupLockedException"/> ; avec <paramref name="direct"/> = true, le fichier d'origine
    /// est ouvert tel quel (sur confirmation explicite de l'utilisateur).
    /// </summary>
    public StoreEntry OpenBackup(string pstPath, bool direct, IProgress<string> progress)
    {
        Connect();
        CloseBackup();
        if (!File.Exists(pstPath)) throw new UserMessageException("Fichier introuvable : " + pstPath);

        if (direct)
        {
            dynamic already = FindStoreByPath(pstPath);
            _backupWasAttached = already != null;
            if (already == null)
            {
                progress.Report("Ouverture directe du fichier de sauvegarde dans Outlook…");
                _ns.AddStoreEx(pstPath, olStoreUnicode);
                already = FindStoreByPath(pstPath);
                if (already == null) throw new UserMessageException("Outlook n'a pas pu ouvrir ce fichier PST.");
            }
            try
            {
                _backupStoreId = already.StoreID;
                _backupCopyPath = null;
                return new StoreEntry { StoreId = _backupStoreId, DisplayName = Path.GetFileName(pstPath), FilePath = pstPath, Description = pstPath };
            }
            finally { Release(already); }
        }

        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DataBridge", "Outlook", "Restauration");
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.GetFiles(dir, "*.pst")) { try { File.Delete(old); } catch { /* encore ouvert : sera supprimé plus tard */ } }

        long size = new FileInfo(pstPath).Length;
        long free = new DriveInfo(Path.GetPathRoot(dir)).AvailableFreeSpace;
        if (free < size + 200L * 1024 * 1024)
            throw new UserMessageException($"Espace disque insuffisant pour la copie de travail ({size / 1048576:N0} Mo nécessaires sur {Path.GetPathRoot(dir)}).");

        progress.Report($"Copie de travail de la sauvegarde ({size / 1048576:N0} Mo)… l'original ne sera pas ouvert.");
        string copy = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".pst");
        try { File.Copy(pstPath, copy, overwrite: false); }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33) // partage / verrouillage
        {
            try { File.Delete(copy); } catch { }
            throw new BackupLockedException();
        }
        File.SetAttributes(copy, FileAttributes.Normal);
        _backupWasAttached = false;

        progress.Report("Ouverture de la copie dans Outlook…");
        _ns.AddStoreEx(copy, olStoreUnicode);
        dynamic store = FindStoreByPath(copy);
        if (store == null)
        {
            try { File.Delete(copy); } catch { }
            throw new UserMessageException("Outlook n'a pas pu ouvrir ce fichier PST.");
        }
        try
        {
            _backupStoreId = store.StoreID;
            _backupCopyPath = copy;
            return new StoreEntry { StoreId = _backupStoreId, DisplayName = Path.GetFileName(pstPath), FilePath = copy, Description = pstPath };
        }
        finally { Release(store); }
    }

    /// <summary>
    /// Retire la sauvegarde d'Outlook (sauf si elle y était déjà avant) ; seule une copie de travail
    /// (fichier temporaire de l'application) est supprimée, jamais le fichier d'origine.
    /// </summary>
    public void CloseBackup()
    {
        if (_backupStoreId == null) return;
        try
        {
            if (_ns != null && !_backupWasAttached)
            {
                dynamic store = FindStore(_backupStoreId);
                if (store != null)
                {
                    dynamic root = store.GetRootFolder();
                    try { _ns.RemoveStore(root); } catch { }
                    Release(root);
                    Release(store);
                }
            }
        }
        catch { }
        string copy = _backupCopyPath;
        _backupStoreId = null;
        _backupCopyPath = null;
        for (int i = 0; i < 10 && copy != null && File.Exists(copy); i++)
        {
            try { File.Delete(copy); }
            catch { Thread.Sleep(500); }
        }
    }

    // ================================================================== RESTAURATION

    public OperationResult Restore(RestoreRequest req, IProgress<ProgressInfo> progress, CancellationToken ct)
    {
        Connect();
        var res = new OperationResult
        {
            Kind = OperationKind.Restore, SourceName = req.BackupFile, TargetName = req.TargetName,
            AutocompleteRequested = req.AutoAsContacts || req.AutoReplace, BackupOpenedDirectly = req.BackupOpenedDirectly,
        };
        res.Mails.Requested = req.MailJobs.Sum(j => j.Mails.Count);
        res.Contacts.Requested = req.Contacts.Count;
        res.Events.Requested = req.Events.Count;

        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        int total = res.Mails.Requested + res.Contacts.Requested + res.Events.Requested + (req.AutoAsContacts ? 1 : 0) + (req.AutoReplace ? 1 : 0);
        var ctx = new CopyContext { SourceStoreId = req.BackupStoreId, Res = res, Ct = ct, Total = total, Progress = progress, SkipDuplicates = req.SkipDuplicates };
        dynamic target = null, targetRoot = null;
        var defaults = new Dictionary<int, object>();

        try
        {
            // DataBridge : une sauvegarde peut ne contenir que l'autocomplétion (pas de PST à ouvrir).
            if (req.BackupStoreId != null)
            {
                object backup = FindStore(req.BackupStoreId);
                if (backup == null) throw new UserMessageException("La sauvegarde n'est plus ouverte. Rouvrez-la.");
                Release(backup);
            }
            target = FindStore(req.TargetStoreId);
            if (target == null) throw new UserMessageException("Boîte cible introuvable. Relancez « Lister les boîtes Outlook ».");
            targetRoot = target.GetRootFolder();
            object targetObj = target, rootObj = targetRoot;

            if (req.IntoNewFolder)
            {
                string name = "Restauration " + DateTime.Now.ToString("dd.MM.yyyy HH'h'mm");
                object sub = FindChild(rootObj, name) ?? AddFolder(rootObj, name, olFolderInbox);
                ctx.Folders["#base"] = sub;
                ctx.Resolve = (f, type) => GetOrCreatePath(sub, f.Path, f.PathSpecial, type, ctx.Folders, tag: false);
                res.Notes.Add($"Les éléments ont été placés dans le dossier « {name} » de la boîte cible.");
            }
            else
            {
                ctx.Resolve = (f, type) => ResolveMerge(targetObj, rootObj, f, type, ctx.Folders, defaults);
            }

            if (req.MailJobs.Count > 0 || req.Contacts.Count > 0 || req.Events.Count > 0)
            {
                progress.Report(new ProgressInfo(0, total, "Restauration en cours…"));
                Directory.CreateDirectory(ctx.TempDir);
                RunCopies(ctx, req.MailJobs, req.Contacts, req.Events);
            }

            if (req.AutoAsContacts || req.AutoReplace)
            {
                ct.ThrowIfCancellationRequested();
                byte[] data = null;
                try
                {
                    data = File.ReadAllBytes(req.AutocompleteFile);
                    if (AutocompleteParser.CountRows(data) == null) { data = null; res.AutocompleteLines.Add("NON restaurée : le fichier n'est pas un cache d'autocomplétion Outlook valide."); }
                }
                catch (Exception ex) { res.AutocompleteLines.Add("NON restaurée : fichier illisible (" + ex.Message + ")."); }

                bool ok = data != null;
                if (req.AutoAsContacts)
                {
                    progress.Report(new ProgressInfo(ctx.Done, total, "Autocomplétion : création des contacts…"));
                    if (data != null) ok &= RestoreAutoContacts(targetObj, rootObj, data, res, ct);
                    ctx.Step("Autocomplétion (contacts)");
                }
                if (req.AutoReplace)
                {
                    progress.Report(new ProgressInfo(ctx.Done, total, "Autocomplétion : remplacement du cache…"));
                    if (data != null) ok &= RestoreAutoStream(targetObj, data, req.ReportDir, stamp, res);
                    ctx.Step("Autocomplétion (cache)");
                }
                res.AutocompleteOk = ok;
            }
        }
        catch (OperationCanceledException) { res.Cancelled = true; }
        catch (Exception ex) { res.Fatal = ex.Message; }
        finally
        {
            ctx.ReleaseAll();
            foreach (var o in defaults.Values) Release(o);
            Release(targetRoot);
            Release(target);
        }

        res.NotProcessed = Math.Max(0, total - ctx.Done);
        res.ReportPath = WriteReport(req.ReportDir, "Compte-rendu_restauration_" + stamp, res);
        return res;
    }

    /// <summary>
    /// Mode « fusion » : les dossiers spéciaux (réception, envoyés, brouillons, supprimés, indésirables,
    /// calendrier, contacts) sont envoyés vers leurs équivalents dans la cible ; les autres sont
    /// retrouvés par nom ou créés.
    /// </summary>
    object ResolveMerge(object target, object root, FolderNode f, int type, Dictionary<string, object> cache, Dictionary<int, object> defaults)
    {
        object cur = root;
        string key = "";
        for (int i = 0; i < f.Path.Count; i++)
        {
            string seg = f.Path[i];
            key += "\\" + seg;
            if (cache.TryGetValue(key, out object hit)) { cur = hit; continue; }
            int sp = i < f.PathSpecial.Count ? f.PathSpecial[i] : 0;
            object next = null;
            if (sp != 0)
            {
                if (!defaults.TryGetValue(sp, out next))
                {
                    try { next = ((dynamic)target).GetDefaultFolder(sp); } catch { next = null; }
                    defaults[sp] = next;
                }
            }
            if (next == null)
            {
                int createType = sp == olFolderCalendar || sp == olFolderContacts ? sp : type;
                next = FindChild(cur, seg, createType) ?? AddFolder(cur, seg, createType);
                cache[key] = next;
            }
            else cache[key] = next;
            cur = next;
        }
        return cur;
    }

    bool RestoreAutoContacts(object target, object root, byte[] data, OperationResult res, CancellationToken ct)
    {
        List<AutocompleteParser.Entry> entries;
        bool complete;
        try { entries = AutocompleteParser.Parse(data, out complete); }
        catch
        {
            res.AutocompleteLines.Add("Contacts : NON créés, le contenu du cache n'a pas pu être décodé.");
            return false;
        }
        if (!complete)
            res.AutocompleteLines.Add($"Contacts : décodage partiel, seules {entries.Count} entrée(s) sur {AutocompleteParser.CountRows(data)} ont pu être lues.");

        object contactsDefault = null, folder = null;
        try
        {
            try { contactsDefault = ((dynamic)target).GetDefaultFolder(olFolderContacts); } catch { }
            object parent = contactsDefault ?? root;
            folder = FindChild(parent, AutoContactsFolder, olFolderContacts) ?? AddFolder(parent, AutoContactsFolder, olFolderContacts);

            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in new[] { folder, contactsDefault }.Where(x => x != null))
                foreach (var r in ReadRows(f, EmailCols))
                    for (int k = 1; k < 4; k++) if (S(r[k]) != "") existing.Add(S(r[k]).Trim());

            int created = 0, dup = 0, noMail = 0, err = 0;
            foreach (var e in entries)
            {
                ct.ThrowIfCancellationRequested();
                string email = (e.Email ?? "").Trim();
                if (!email.Contains('@')) { noMail++; continue; }
                if (existing.Contains(email)) { dup++; continue; }
                dynamic items = null, c = null;
                try
                {
                    items = ((dynamic)folder).Items;
                    c = items.Add("IPM.Contact");
                    c.FullName = e.Name != "" ? e.Name : email;
                    c.Email1Address = email;
                    c.Save(); // nouvel élément dans la cible : aucun envoi
                    existing.Add(email);
                    created++;
                }
                catch (Exception ex)
                {
                    err++;
                    res.Details.Add($"Erreur — contact d'autocomplétion « {email} » : {ex.Message}");
                }
                finally { Release(c); Release(items); }
            }
            res.AutocompleteLines.Add($"Contacts : {created} créé(s) dans « {AutoContactsFolder} », {dup} déjà présent(s), "
                                      + $"{noMail} sans adresse e-mail exploitable, {err} erreur(s).");
            return err == 0 && complete;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            res.AutocompleteLines.Add("Contacts : NON créés (" + ex.Message + ").");
            return false;
        }
        finally { Release(folder); Release(contactsDefault); }
    }

    bool RestoreAutoStream(object target, byte[] data, string reportDir, string stamp, OperationResult res)
    {
        dynamic inbox = null, storage = null, pa = null;
        try
        {
            // 1. Copie de sécurité du cache actuel de la cible (sinon on n'écrit rien).
            var (existing, _) = ReadAutocomplete(target);
            if (existing != null)
            {
                string bak = UniquePath(reportDir, "Autocompletion_cible_avant_restauration_" + stamp, ".bin");
                WriteNew(bak, existing);
                if (new FileInfo(bak).Length != existing.Length)
                {
                    res.AutocompleteLines.Add("Cache : NON remplacé, la copie de sécurité du cache actuel a échoué.");
                    return false;
                }
                res.AutocompleteFiles.Add(bak);
            }

            // 2. Écriture.
            try { inbox = ((dynamic)target).GetDefaultFolder(olFolderInbox); }
            catch
            {
                res.AutocompleteLines.Add("Cache : NON remplacé, la boîte cible n'a pas de « Boîte de réception » reconnue.");
                return false;
            }
            storage = inbox.GetStorage(AutocompleteClass, olIdentifyByMessageClass);
            pa = storage.PropertyAccessor;
            pa.SetProperty(PR_ROAMING_BINARYSTREAM, data);
            storage.Save();

            // 3. Vérification par relecture.
            var (check, _) = ReadAutocomplete(target);
            if (check == null || !check.AsSpan().SequenceEqual(data))
            {
                res.AutocompleteLines.Add("Cache : écrit, mais la relecture ne correspond pas : à considérer comme NON restauré.");
                return false;
            }

            string defaultId = Str(() => _ns.DefaultStore.StoreID);
            string targetId = Str(() => ((dynamic)target).StoreID);
            res.AutocompleteLines.Add("Cache : remplacé et relu avec succès. Effet à confirmer après redémarrage d'Outlook "
                                      + "(Outlook peut réécrire son ancien cache s'il l'avait déjà en mémoire).");
            if (defaultId != targetId)
                res.AutocompleteLines.Add("Attention : la boîte cible n'est pas la boîte par défaut du profil ; Outlook n'utilise en principe que le cache de la boîte par défaut.");
            if (existing != null) res.Notes.Add("L'ancien cache d'autocomplétion de la boîte cible a été copié avant remplacement (voir le fichier .bin indiqué).");
            return true;
        }
        catch (Exception ex)
        {
            res.AutocompleteLines.Add("Cache : NON remplacé (" + ex.Message + ").");
            return false;
        }
        finally { Release(pa); Release(storage); Release(inbox); }
    }

    // ================================================================== copie commune (sauvegarde et restauration)

    sealed class CopyContext
    {
        public string SourceStoreId;
        public string TempDir = Path.Combine(Path.GetTempPath(), "DataBridge_Outlook_" + Guid.NewGuid().ToString("N"));
        public OperationResult Res;
        public CancellationToken Ct;
        public IProgress<ProgressInfo> Progress;
        public int Total, Done, Counter;
        public bool SkipDuplicates;
        public Func<FolderNode, int, object> Resolve;
        public readonly Dictionary<string, object> Folders = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, HashSet<string>> DupSets = new(StringComparer.OrdinalIgnoreCase);

        public void Step(string text)
        {
            Done++;
            Progress.Report(new ProgressInfo(Done, Total, text));
        }

        public void ReleaseAll()
        {
            foreach (var o in Folders.Values) Release(o);
            Folders.Clear();
            try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, true); } catch { }
        }
    }

    void RunCopies(CopyContext x, List<MailJob> mailJobs, List<ContactEntry> contacts, List<EventEntry> events)
    {
        foreach (var job in mailJobs)
            CopyBatch(x, job.Folder, job.Mails, olFolderInbox, 0, x.Res.Mails, "Mails",
                m => m.EntryId, m => MailKey(m.Subject, m.Sender, m.Date),
                m => $"Mail « {Trunc(m.Subject)} » ({job.Folder.PathText}, {m.Date:dd.MM.yyyy})");

        foreach (var g in contacts.GroupBy(c => c.Folder))
            CopyBatch(x, g.Key, g.ToList(), olFolderContacts, 1, x.Res.Contacts, "Contacts",
                c => c.EntryId, c => ContactKey(c.Name, c.Email), c => $"Contact « {Trunc(c.Name)} » ({g.Key.PathText})");

        foreach (var g in events.GroupBy(e => e.Folder))
            CopyBatch(x, g.Key, g.ToList(), olFolderCalendar, 2, x.Res.Events, "Calendrier",
                e => e.EntryId, e => EventKey(e.Subject, e.Start),
                e => (e.IsRecurring ? "Série" : "Événement") + $" « {Trunc(e.Subject)} » ({e.Start:dd.MM.yyyy}, {g.Key.PathText})");
    }

    void CopyBatch<T>(CopyContext x, FolderNode folder, List<T> items, int folderType, int kind, Counts counts, string stepName,
                      Func<T, string> idOf, Func<T, string> keyOf, Func<T, string> labelOf)
    {
        x.Ct.ThrowIfCancellationRequested();
        string step = stepName + " : " + folder.PathText;
        object dest;
        try { dest = x.Resolve(folder, folderType); }
        catch (Exception ex)
        {
            counts.Errors += items.Count;
            x.Res.Details.Add($"Erreur — dossier « {folder.PathText} » non créé dans la destination ({ex.Message}) : {items.Count} élément(s) non copié(s).");
            foreach (var _ in items) x.Step(step);
            return;
        }

        HashSet<string> dups = x.SkipDuplicates && items.Count > 0 ? DupSet(x, dest, kind) : null;
        foreach (var it in items)
        {
            x.Ct.ThrowIfCancellationRequested();
            if (dups != null && dups.Contains(keyOf(it))) counts.Duplicates++;
            else CopyOne(idOf(it), x, dest, counts, labelOf(it), appointment: kind == 2);
            x.Step(step);
        }
    }

    /// <summary>Empreintes des éléments déjà présents dans un dossier de destination (avant restauration).</summary>
    static HashSet<string> DupSet(CopyContext x, object dest, int kind)
    {
        string id = Str(() => ((dynamic)dest).EntryID) + "|" + kind;
        if (x.DupSets.TryGetValue(id, out var set)) return set;
        set = new HashSet<string>();
        try
        {
            foreach (var r in ReadRows(dest, kind == 0 ? MailCols : kind == 1 ? ContactCols : EventCols))
                set.Add(kind == 0 ? MailKey(S(r[1]), S(r[2]), D(r[3]) ?? D(r[4]))
                        : kind == 1 ? ContactKey(ContactName(r[1], r[5]), S(r[2]))
                        : EventKey(S(r[1]), D(r[2]) ?? DateTime.MinValue));
        }
        catch { }
        x.DupSets[id] = set;
        return set;
    }

    static string MailKey(string subject, string sender, DateTime? date) =>
        $"{subject}\u0001{sender}\u0001{date:yyyyMMddHHmm}".ToLowerInvariant();
    static string ContactKey(string name, string email) => $"{name}\u0001{email}".ToLowerInvariant();
    static string EventKey(string subject, DateTime start) => $"{subject}\u0001{start:yyyyMMddHHmm}".ToLowerInvariant();

    /// <summary>
    /// Copie un élément sans toucher à l'original.
    ///  - Mails et contacts : SaveAs .msg (lecture), réouverture du .msg hors de toute boîte,
    ///    puis déplacement de cette copie dans le dossier de destination.
    ///  - Rendez-vous : Outlook refuse ce déplacement ; on utilise AppointmentItem.CopyTo en mode
    ///    « créer un rendez-vous » (olCreateAppointment), qui écrit directement dans la destination
    ///    sans modifier l'original ni envoyer quoi que ce soit (récurrence, exceptions, participants
    ///    et pièces jointes conservés — vérifié).
    /// </summary>
    void CopyOne(string entryId, CopyContext x, object dest, Counts counts, string label, bool appointment = false)
    {
        dynamic src = null, copy = null, moved = null;
        string tmp = Path.Combine(x.TempDir, ++x.Counter + ".msg");
        try
        {
            try { src = _ns.GetItemFromID(entryId, x.SourceStoreId); }
            catch (Exception ex) when (ex.HResult == MAPI_E_NOT_FOUND || ex.InnerException?.HResult == MAPI_E_NOT_FOUND)
            {
                counts.Ignored++;
                x.Res.Details.Add($"Ignoré — {label} : introuvable (supprimé ou déplacé depuis le chargement).");
                return;
            }
            if (appointment && Str(() => src.MessageClass).StartsWith("IPM.Appointment", StringComparison.OrdinalIgnoreCase))
            {
                moved = src.CopyTo(dest, olCreateAppointment);
                counts.Saved++;
                return;
            }
            src.SaveAs(tmp, olMSGUnicode);
            Release(src);
            src = null;

            copy = _ns.OpenSharedItem(tmp);
            moved = copy.Move(dest);
            counts.Saved++;
        }
        catch (Exception ex)
        {
            counts.Errors++;
            x.Res.Details.Add($"Erreur — {label} : {ex.Message}");
        }
        finally
        {
            Release(moved);
            Release(copy);
            Release(src);
            try { File.Delete(tmp); } catch { }
        }
    }

    // ================================================================== dossiers et boîtes

    static object GetOrCreatePath(object root, List<string> path, List<int> special, int type, Dictionary<string, object> cache, bool tag)
    {
        object cur = root;
        string key = "";
        for (int i = 0; i < path.Count; i++)
        {
            string seg = path[i];
            key += "\\" + seg;
            if (cache.TryGetValue(key, out object hit)) { cur = hit; continue; }
            int sp = i < special.Count ? special[i] : 0;
            int createType = sp == olFolderCalendar || sp == olFolderContacts ? sp : type;
            object child = FindChild(cur, seg, createType) ?? AddFolder(cur, seg, createType);
            if (tag && sp != 0) SetTag(child, PropSpecial, sp.ToString());
            cache[key] = child;
            cur = child;
        }
        return cur;
    }

    static object AddFolder(object parent, string name, int type)
    {
        dynamic folders = ((dynamic)parent).Folders;
        try { return folders.Add(name, type); }
        finally { Release(folders); }
    }

    /// <summary>
    /// Cherche un sous-dossier par nom. Pour un agenda ou des contacts, accepte aussi le nom suivi
    /// du suffixe qu'Outlook ajoute dans les boîtes IMAP (« Calendrier (uniquement cet ordinateur) »).
    /// </summary>
    static object FindChild(object parentObj, string name, int folderType = -1)
    {
        dynamic folders = ((dynamic)parentObj).Folders;
        object loose = null;
        try
        {
            int n = folders.Count;
            for (int i = 1; i <= n; i++)
            {
                dynamic f = folders.Item(i);
                string fn = f.Name;
                if (string.Equals(fn, name, StringComparison.OrdinalIgnoreCase)) { Release(loose); return f; }
                if (loose == null && (folderType == olFolderCalendar || folderType == olFolderContacts)
                    && fn.StartsWith(name + " (", StringComparison.OrdinalIgnoreCase) && fn.EndsWith(")")
                    && Int(() => f.DefaultItemType, -1) == (folderType == olFolderCalendar ? olAppointmentItem : olContactItem))
                {
                    loose = f;
                    continue;
                }
                Release(f);
            }
            return loose;
        }
        finally { Release(folders); }
    }

    object FindStore(string storeId)
    {
        dynamic stores = _ns.Stores;
        try
        {
            int n = stores.Count;
            for (int i = 1; i <= n; i++)
            {
                dynamic s = stores.Item(i);
                if (Str(() => s.StoreID) == storeId) return s;
                Release(s);
            }
            return null;
        }
        finally { Release(stores); }
    }

    object FindStoreByPath(string path)
    {
        string full = Path.GetFullPath(path);
        dynamic stores = _ns.Stores;
        try
        {
            int n = stores.Count;
            for (int i = 1; i <= n; i++)
            {
                dynamic s = stores.Item(i);
                string fp = Str(() => s.FilePath);
                bool match = false;
                try { match = fp != "" && string.Equals(Path.GetFullPath(fp), full, StringComparison.OrdinalIgnoreCase); } catch { }
                if (match) return s;
                Release(s);
            }
            return null;
        }
        finally { Release(stores); }
    }

    static string DefaultFolderId(object storeObj, int type)
    {
        dynamic f = null;
        try { f = ((dynamic)storeObj).GetDefaultFolder(type); return (string)f.EntryID; }
        catch { return null; }
        finally { Release(f); }
    }

    static bool IsHidden(object folderObj)
    {
        dynamic pa = null;
        try
        {
            pa = ((dynamic)folderObj).PropertyAccessor;
            return pa.GetProperty(PR_ATTR_HIDDEN) is bool b && b;
        }
        catch { return false; }
        finally { Release(pa); }
    }

    static string GetTag(object obj, string prop)
    {
        dynamic pa = null;
        try
        {
            pa = ((dynamic)obj).PropertyAccessor;
            return pa.GetProperty(prop) as string;
        }
        catch { return null; }
        finally { Release(pa); }
    }

    /// <summary>Marque un dossier du PST de sauvegarde (jamais appelé sur une boîte source ou cible).</summary>
    static void SetTag(object obj, string prop, string value)
    {
        dynamic pa = null;
        try
        {
            pa = ((dynamic)obj).PropertyAccessor;
            pa.SetProperty(prop, value);
        }
        catch { }
        finally { Release(pa); }
    }

    // ================================================================== utilitaires

    static string WriteReport(string dir, string baseName, OperationResult res)
    {
        try
        {
            string path = UniquePath(dir, baseName, ".txt");
            res.ReportPath = path;
            WriteNew(path, Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(res.ToText())).ToArray());
            return path;
        }
        catch
        {
            res.ReportPath = null;
            return null;
        }
    }

    /// <summary>Chemin libre : ajoute _2, _3… si le fichier existe déjà. Jamais d'écrasement.</summary>
    static string UniquePath(string dir, string baseName, string ext)
    {
        string p = Path.Combine(dir, baseName + ext);
        for (int i = 2; File.Exists(p); i++) p = Path.Combine(dir, $"{baseName}_{i}{ext}");
        return p;
    }

    static void WriteNew(string path, byte[] data)
    {
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write); // échoue si le fichier existe
        fs.Write(data, 0, data.Length);
    }

    static void Release(object o)
    {
        try { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); } catch { }
    }

    static object[] ToArray(object o)
    {
        if (o is not Array a) return Array.Empty<object>();
        var r = new object[a.Length];
        int i = 0;
        foreach (var x in a) r[i++] = x;
        return r;
    }

    static string S(object o) => o == null || o is DBNull ? "" : o.ToString();
    static bool B(object o) => o is bool b && b;
    static DateTime? D(object o) => o is DateTime d && d.Year > 1601 && d.Year < 4000 ? d : null;
    static string ContactName(object fullName, object subject) => S(fullName) != "" ? S(fullName) : S(subject);

    static string Str(Func<object> f)
    {
        try { return S(f()); } catch { return ""; }
    }

    static int Int(Func<object> f, int def)
    {
        try { return Convert.ToInt32(f()); } catch { return def; }
    }

    static string Trunc(string s) => string.IsNullOrEmpty(s) ? "(sans titre)" : s.Length > 80 ? s[..80] + "…" : s;

    static string Csv(string s)
    {
        s ??= "";
        return s.IndexOfAny(new[] { ';', '"', '\r', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    static string AccountTypeName(int t) => t switch
    {
        0 => "Exchange",
        1 => "IMAP",
        2 => "POP3",
        3 => "HTTP",
        4 => "Exchange ActiveSync",
        _ => "autre",
    };

    static string RecurrenceText(int t) => t switch
    {
        0 => "Quotidienne",
        1 => "Hebdomadaire",
        2 or 3 => "Mensuelle",
        5 or 6 => "Annuelle",
        _ => "Récurrente",
    };
}
