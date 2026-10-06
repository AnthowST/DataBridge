using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DataBridge.Core.Copy;
using DataBridge.Core.IO;
using DataBridge.Core.Native;

namespace DataBridge.Core.Restore;

/// <summary>
/// Executes exactly a <see cref="RestorePlan"/>. Files are written with CopyFileEx (dates and attributes preserved).
/// A file that is replaced is first written beside the old one, then swapped, so a cancellation or an error never
/// leaves a half-written file in place of the original. One global conflict policy applies (no prompt per file);
/// particular cases go to the log. Every write is checked to stay under the destination of its item.
/// Cancellation takes effect between files (a very large file in progress is abandoned, the original kept).
/// </summary>
public sealed class RestoreEngine
{
    private const long AbortCurrentFileThreshold = 256L * 1024 * 1024;
    private const string TempSuffix = ".databridge-tmp";
    private const int ERROR_FILE_NOT_FOUND = 2;
    private const int ERROR_PATH_NOT_FOUND = 3;
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_SHARING_VIOLATION = 32;
    private const int ERROR_LOCK_VIOLATION = 33;
    private const int ERROR_HANDLE_DISK_FULL = 39;
    private const int ERROR_FILE_EXISTS = 80;
    private const int ERROR_DISK_FULL = 112;
    private const int ERROR_FILENAME_EXCED_RANGE = 206;
    private const int ERROR_REQUEST_ABORTED = 1235;

    private readonly RestorePlan _plan;
    private readonly RestoreProgress _progress;
    private readonly NativeMethods.CopyProgressRoutine _callback;
    private readonly HashSet<string> _brokenDirectories = new(PathUtil.Comparer);
    private CancellationToken _ct;
    private RestoreResult _result = null!;
    private RestoreLogWriter? _log;
    private bool _abortedCurrent;
    private string? _fatal;

    public RestoreEngine(RestorePlan plan, RestoreProgress progress)
    {
        _plan = plan;
        _progress = progress;
        _callback = OnCopyProgress; // kept alive for every native call
    }

    public Task<RestoreResult> RunAsync(CancellationToken ct) =>
        Task.Factory.StartNew(() => Run(ct), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private bool StopRequested => _fatal != null || _ct.IsCancellationRequested;

    public RestoreResult Run(CancellationToken ct)
    {
        _ct = ct;
        var watch = Stopwatch.StartNew();
        _result = new RestoreResult { StartedAt = DateTime.Now, SafetyRoot = _plan.SafetyRoot };

        // Exact pre-scan with the engine's rules: progress denominators and final control.
        _progress.Phase = "Analyse";
        try
        {
            var analysis = RestorePlanner.Analyze(_plan, ct, n => Interlocked.Exchange(ref _progress.CountedFiles, n));
            _result.Analysis = analysis;
            _progress.PlannedFiles = analysis.WriteFiles;
            _progress.PlannedBytes = analysis.WriteBytes;
            if (!analysis.SpaceOk)
            {
                var v = analysis.Volumes.First(x => !x.Sufficient);
                _fatal = $"Espace insuffisant sur {v.Root.TrimEnd('\\')} ({Format.Size(v.NeededBytes)} nécessaires).";
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled during the analysis: nothing written, the log still records it.
        }
        catch (Exception ex)
        {
            _fatal = "Analyse de la restauration impossible : " + ex.Message;
        }
        _progress.Counting = false;

        try
        {
            _log = new RestoreLogWriter(_plan.Target.LogsFolder, _result.StartedAt);
        }
        catch (Exception ex)
        {
            _fatal ??= "Impossible de créer le journal de restauration : " + ex.Message;
        }

        _progress.Phase = "Restauration";
        _progress.WifiTotal = _plan.Wifi.Count;
        try
        {
            foreach (var item in _plan.Items)
            {
                if (StopRequested) break;
                var ir = new RestoreItemResult(item) { Started = true };
                _result.Items.Add(ir);
                _progress.Category = item.Category;
                _progress.Item = item.Label;
                RunItem(item, ir);
                if (item.Browser?.Firefox is { } registration && !StopRequested && ir.Errors == 0)
                {
                    try
                    {
                        ir.Registration = BrowserRestoreService.RegisterFirefoxProfile(registration, item.SafetyFolder);
                        if (item.SafetyFolder != null && Directory.Exists(item.SafetyFolder)) _result.SafetyUsed = true;
                    }
                    catch (Exception ex)
                    {
                        ir.Registration = "profiles.ini non mis à jour : " + ex.Message;
                        Issue(IssueKind.WriteError, IssueSeverity.Error, Path.Combine(registration.FirefoxRoot, "profiles.ini"), ir.Registration);
                    }
                }
                if (item.Browser != null && ir.Errors == 0 && !StopRequested)
                    Issue(IssueKind.Restored, IssueSeverity.Info, item.DestinationPath,
                        $"Profil navigateur restauré : {item.Browser.SourceDisplayName} → {item.Browser.TargetLabel}.");
            }

            if (!StopRequested) RunWifi();
            if (!StopRequested && _plan.Wallpaper is { } wallpaper) RunWallpaper(wallpaper);
        }
        catch (OperationCanceledException)
        {
            // handled below
        }
        catch (Exception ex)
        {
            _fatal ??= "Erreur inattendue : " + ex.Message;
        }

        _result.Outcome = _fatal != null ? RestoreOutcome.Failed : ct.IsCancellationRequested ? RestoreOutcome.Cancelled : RestoreOutcome.Completed;
        _result.FatalMessage = _fatal;
        _result.RestoredFiles = Interlocked.Read(ref _progress.RestoredFiles);
        _result.ReplacedFiles = Interlocked.Read(ref _progress.ReplacedFiles);
        _result.WrittenBytes = Interlocked.Read(ref _progress.WrittenBytes);
        _result.KeptFiles = _progress.KeptFiles;
        _result.IdenticalFiles = _progress.IdenticalFiles;
        _result.ErrorFiles = _progress.ErrorFiles;
        _result.ErrorFolders = _progress.ErrorFolders;
        _result.SkippedCount = _progress.Skipped;
        _result.InterruptedFiles = _progress.InterruptedFiles;
        _result.Duration = watch.Elapsed;

        _progress.Phase = "Journal";
        _progress.CurrentFile = "";
        try
        {
            _result.LogPath = _log?.Complete(_plan, _result);
        }
        catch (Exception ex)
        {
            _result.FatalMessage ??= "Journal non écrit : " + ex.Message;
        }
        _progress.Phase = "Terminé";
        return _result;
    }

    // ------------------------------------------------------------------ items

    private void RunItem(RestoreItem item, RestoreItemResult ir)
    {
        // Defence in depth: the plan was validated, but an item outside the backup is never read.
        if (!RestorePaths.IsInside(item.SourcePath, _plan.Backup.Root) || RestorePaths.NormalizeAbsolute(item.DestinationPath) is null)
        {
            ir.ErrorFolders++;
            _progress.ErrorFolders++;
            Issue(IssueKind.UnsafePath, IssueSeverity.Error, item.SourcePath, "Élément refusé par sécurité : source ou destination non sûre.");
            return;
        }

        IEnumerator<WalkEvent> walk;
        try
        {
            walk = RestoreWalker.Walk(item, _ct).GetEnumerator();
        }
        catch (Exception ex)
        {
            FolderError(IssueKind.ReadError, item.SourcePath, ex.Message, ir);
            return;
        }

        using (walk)
        {
            while (!StopRequested)
            {
                bool moved;
                try
                {
                    moved = walk.MoveNext();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!SourceAlive()) Fatal("La sauvegarde n'est plus accessible (support débranché ?).");
                    FolderError(IssueKind.ReadError, item.SourcePath, "Lecture de la sauvegarde interrompue : " + ex.Message, ir);
                    break;
                }
                if (!moved) break;
                var e = walk.Current;
                switch (e.Kind)
                {
                    case WalkKind.EnterDirectory:
                        EnsureDirectory(e.DestinationPath, item, ir);
                        break;
                    case WalkKind.Skipped:
                        _progress.Skipped++;
                        ir.Skipped++;
                        Issue(e.Issue, IssueSeverity.Ignored, e.SourcePath, e.Message ?? "Non restauré.");
                        break;
                    case WalkKind.Error:
                        if (e.Source is { IsDirectory: false }) FileError(e.Issue, e.DestinationPath, e.Message ?? "Erreur.", e.Source.Size, ir);
                        else FolderError(e.Issue, e.SourcePath, e.Message ?? "Erreur.", ir);
                        break;
                    case WalkKind.File:
                        RestoreFile(item, e, ir);
                        break;
                }
            }
        }
    }

    private void RestoreFile(RestoreItem item, WalkEvent e, RestoreItemResult ir)
    {
        var source = e.Source!;
        var destination = e.DestinationPath;
        if (!item.IsFile && !RestorePaths.IsInside(destination, item.AllowedRoot))
        {
            _progress.Skipped++;
            ir.Skipped++;
            Issue(IssueKind.UnsafePath, IssueSeverity.Ignored, e.SourcePath, "Destination hors du dossier prévu : refusée par sécurité.");
            return;
        }

        switch (ConflictResolver.Decide(item.Policy, source.Size, source.LastWriteUtc, e.Existing))
        {
            case FileDecision.Identical:
                _progress.IdenticalFiles++;
                ir.Identical++;
                return;
            case FileDecision.Keep:
                _progress.KeptFiles++;
                ir.Kept++;
                Issue(IssueKind.KeptExisting, IssueSeverity.Info, destination,
                    item.Policy == ConflictPolicy.Keep ? "Fichier déjà présent : conservé." : "Fichier déjà présent et plus récent (ou même date) : conservé.");
                return;
        }

        var parent = PathUtil.Parent(destination);
        if (parent != null && _brokenDirectories.Contains(parent))
        {
            FileError(IssueKind.WriteError, destination, "Dossier de destination non créé : fichier non restauré.", source.Size, ir);
            return;
        }

        if (e.Existing is null) CopyNew(e.SourcePath, destination, source.Size, e.Relative, item, ir);
        else Replace(e.SourcePath, destination, source.Size, e.Existing, e.Relative, item, ir);
    }

    private void CopyNew(string source, string destination, long size, string relative, RestoreItem item, RestoreItemResult ir)
    {
        var error = Copy(source, destination, failIfExists: true);
        if (error == 0)
        {
            Interlocked.Increment(ref _progress.RestoredFiles);
            Interlocked.Add(ref _progress.WrittenBytes, size);
            ir.Restored++;
            ir.WrittenBytes += size;
            // A database restored where only an old journal was left must not be paired with that journal.
            if (item.CleanDatabaseCompanions) MoveStaleCompanions(source, destination, relative, item);
            return;
        }
        if (error == ERROR_REQUEST_ABORTED && _abortedCurrent)
        {
            Interrupted(destination, size, ir);
            return;
        }
        if (error == ERROR_FILE_EXISTS)
        {
            // Created meanwhile by another program: never overwritten without the policy's decision.
            _progress.KeptFiles++;
            ir.Kept++;
            Issue(IssueKind.KeptExisting, IssueSeverity.Info, destination, "Fichier apparu à la destination pendant la restauration : conservé.");
            return;
        }
        ReportFailure(error, destination, size, ir);
    }

    /// <summary>Safety copy (when requested), write beside the original, then swap: the original survives any failure.</summary>
    private void Replace(string source, string destination, long size, FsEntry existing, string relative, RestoreItem item, RestoreItemResult ir)
    {
        if (item.SafetyFolder != null)
        {
            try
            {
                var safety = Path.Combine(item.SafetyFolder, relative);
                Directory.CreateDirectory(PathUtil.ToExtended(PathUtil.Parent(safety)!));
                File.Copy(PathUtil.ToExtended(destination), PathUtil.ToExtended(safety), overwrite: true);
                _result.SafetyUsed = true;
            }
            catch (Exception ex)
            {
                FileError(IssueKind.WriteError, destination, "Copie de sécurité impossible, fichier non remplacé : " + ex.Message, size, ir);
                return;
            }
        }

        var temp = destination + TempSuffix;
        TryDelete(temp);
        var error = Copy(source, temp, failIfExists: true);
        if (error != 0)
        {
            TryDelete(temp);
            if (error == ERROR_REQUEST_ABORTED && _abortedCurrent) Interrupted(destination, size, ir);
            else ReportFailure(error, destination, size, ir);
            return;
        }

        try
        {
            var attributes = File.GetAttributes(PathUtil.ToExtended(destination));
            if ((attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0)
                File.SetAttributes(PathUtil.ToExtended(destination), FileAttributes.Normal);
            File.Move(PathUtil.ToExtended(temp), PathUtil.ToExtended(destination), overwrite: true);
        }
        catch (Exception ex)
        {
            TryDelete(temp);
            var code = ex.HResult & 0xFFFF;
            ReportFailure(code != 0 ? code : ERROR_ACCESS_DENIED, destination, size, ir);
            return;
        }

        Interlocked.Increment(ref _progress.ReplacedFiles);
        Interlocked.Add(ref _progress.WrittenBytes, size);
        ir.Replaced++;
        ir.WrittenBytes += size;
        Issue(IssueKind.Replaced, IssueSeverity.Info, destination,
            $"Remplacé par la version de la sauvegarde ({Format.Size(existing.Size)} → {Format.Size(size)})"
            + (item.SafetyFolder != null ? ", copie de sécurité conservée." : "."));

        if (item.CleanDatabaseCompanions) MoveStaleCompanions(source, destination, relative, item);
    }

    /// <summary>A restored browser database must not keep a stale journal / WAL file of this PC: it is moved to the safety folder.</summary>
    private void MoveStaleCompanions(string source, string destination, string relative, RestoreItem item)
    {
        foreach (var suffix in new[] { "-journal", "-wal", "-shm" })
        {
            var companion = destination + suffix;
            if (!File.Exists(PathUtil.ToExtended(companion)) || File.Exists(PathUtil.ToExtended(source + suffix))) continue;
            try
            {
                if (item.SafetyFolder != null)
                {
                    var safety = Path.Combine(item.SafetyFolder, relative + suffix);
                    Directory.CreateDirectory(PathUtil.ToExtended(PathUtil.Parent(safety)!));
                    File.Move(PathUtil.ToExtended(companion), PathUtil.ToExtended(safety), overwrite: true);
                    _result.SafetyUsed = true;
                }
                else
                {
                    File.Delete(PathUtil.ToExtended(companion));
                }
            }
            catch (Exception ex)
            {
                Issue(IssueKind.WriteError, IssueSeverity.Ignored, companion, "Fichier compagnon de base de données non déplacé : " + ex.Message);
            }
        }
    }

    private int Copy(string source, string destination, bool failIfExists)
    {
        _progress.CurrentFile = destination;
        Interlocked.Exchange(ref _progress.CurrentFileBytes, 0);
        _abortedCurrent = false;
        var cancel = 0;
        var flags = NativeMethods.COPY_FILE_ALLOW_DECRYPTED_DESTINATION | (failIfExists ? NativeMethods.COPY_FILE_FAIL_IF_EXISTS : 0);
        var ok = NativeMethods.CopyFileExW(PathUtil.ToExtended(source), PathUtil.ToExtended(destination), _callback, IntPtr.Zero, ref cancel, flags);
        var error = ok ? 0 : Marshal.GetLastWin32Error();
        Interlocked.Exchange(ref _progress.CurrentFileBytes, 0);
        return error;
    }

    private uint OnCopyProgress(long totalFileSize, long totalBytesTransferred, long streamSize, long streamBytesTransferred,
        uint streamNumber, uint callbackReason, IntPtr sourceFile, IntPtr destinationFile, IntPtr data)
    {
        Interlocked.Exchange(ref _progress.CurrentFileBytes, totalBytesTransferred);
        if (_ct.IsCancellationRequested && totalFileSize - totalBytesTransferred > AbortCurrentFileThreshold)
        {
            _abortedCurrent = true;
            return NativeMethods.PROGRESS_CANCEL; // Windows deletes the partial (new or temporary) file
        }
        return NativeMethods.PROGRESS_CONTINUE;
    }

    private void EnsureDirectory(string path, RestoreItem item, RestoreItemResult ir)
    {
        if (!item.IsFile && !RestorePaths.IsInside(path, item.AllowedRoot))
        {
            _brokenDirectories.Add(path);
            Issue(IssueKind.UnsafePath, IssueSeverity.Error, path, "Dossier hors de la destination prévue : refusé par sécurité.");
            return;
        }
        try
        {
            Directory.CreateDirectory(PathUtil.ToExtended(path));
        }
        catch (Exception ex)
        {
            _brokenDirectories.Add(path);
            var code = ex.HResult & 0xFFFF;
            if (code is ERROR_DISK_FULL or ERROR_HANDLE_DISK_FULL) Fatal("Espace insuffisant sur la destination.");
            FolderError(code == ERROR_ACCESS_DENIED ? IssueKind.AccessDenied : IssueKind.WriteError, path,
                code == ERROR_ACCESS_DENIED ? "Dossier non créé : accès refusé avec les droits de la session." : "Création du dossier impossible : " + ex.Message, ir);
        }
    }

    // ------------------------------------------------------------------ Wi-Fi, wallpaper

    private void RunWifi()
    {
        if (_plan.Wifi.Count == 0) return;
        _progress.Category = RestoreCategories.Network;
        _progress.Item = "Profils Wi-Fi";
        foreach (var w in _plan.Wifi)
        {
            if (StopRequested) break;
            _progress.CurrentFile = "Profil Wi-Fi : " + w.Profile.Name;
            var r = WifiRestoreService.Import(w.Profile, _plan.WifiExisting);
            _result.Wifi.Add(r);
            _progress.WifiDone++;
            var (kind, severity) = r.Outcome switch
            {
                WifiRestoreOutcome.Imported or WifiRestoreOutcome.Replaced => (IssueKind.Restored, IssueSeverity.Info),
                WifiRestoreOutcome.KeptExisting => (IssueKind.KeptExisting, IssueSeverity.Info),
                WifiRestoreOutcome.NotRestored => (IssueKind.NotRestored, IssueSeverity.Ignored),
                _ => (IssueKind.Other, IssueSeverity.Error),
            };
            // Name only: the key possibly contained in the XML is never written anywhere.
            Issue(kind, severity, "Wi-Fi « " + w.Profile.Name + " »", r.Message);
        }
    }

    private void RunWallpaper(WallpaperRestoreItem item)
    {
        _progress.Category = RestoreCategories.Windows;
        _progress.Item = "Fond d'écran";
        _progress.CurrentFile = item.TranscodedPath;
        var r = WallpaperRestoreService.Restore(item, Path.Combine(_plan.SafetyRoot, "Wallpaper"));
        _result.Wallpaper = r;
        if (r.Copied && Directory.Exists(Path.Combine(_plan.SafetyRoot, "Wallpaper"))) _result.SafetyUsed = true;
        Issue(r.Copied ? IssueKind.Restored : IssueKind.WriteError, r.Copied ? (r.Applied ? IssueSeverity.Info : IssueSeverity.Ignored) : IssueSeverity.Error,
            "Fond d'écran", r.Message);
    }

    // ------------------------------------------------------------------ errors and counters

    private void ReportFailure(int error, string path, long size, RestoreItemResult ir)
    {
        if (error is ERROR_DISK_FULL or ERROR_HANDLE_DISK_FULL)
        {
            Fatal("Espace insuffisant sur la destination.");
            FileError(IssueKind.WriteError, path, "Disque de destination plein.", size, ir);
            return;
        }
        if (!SourceAlive())
        {
            Fatal("La sauvegarde n'est plus accessible (support débranché ?).");
            FileError(IssueKind.ReadError, path, "Sauvegarde inaccessible.", size, ir);
            return;
        }
        var (kind, message) = error switch
        {
            ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION => (IssueKind.Locked, "Fichier de destination ouvert dans un autre programme : non restauré."),
            ERROR_ACCESS_DENIED => (IssueKind.AccessDenied, "Accès refusé avec les droits de la session : non restauré."),
            ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND => (IssueKind.NotFound, "Fichier ou dossier introuvable pendant la restauration."),
            ERROR_FILENAME_EXCED_RANGE => (IssueKind.PathTooLong, "Chemin ou nom trop long."),
            _ => (IssueKind.Other, new Win32Exception(error).Message),
        };
        FileError(kind, path, message, size, ir);
    }

    private bool SourceAlive()
    {
        try
        {
            return Directory.Exists(_plan.Backup.Root);
        }
        catch
        {
            return false;
        }
    }

    private void Fatal(string message) => _fatal ??= message;

    private void FileError(IssueKind kind, string path, string message, long size, RestoreItemResult ir)
    {
        _progress.ErrorFiles++;
        Interlocked.Add(ref _progress.FailedBytes, size);
        ir.ErrorFiles++;
        Issue(kind, IssueSeverity.Error, path, message);
    }

    private void FolderError(IssueKind kind, string path, string message, RestoreItemResult ir)
    {
        _progress.ErrorFolders++;
        ir.ErrorFolders++;
        Issue(kind, IssueSeverity.Error, path, message);
    }

    private void Interrupted(string path, long size, RestoreItemResult ir)
    {
        _progress.InterruptedFiles++;
        Interlocked.Add(ref _progress.FailedBytes, size);
        ir.Interrupted++;
        Issue(IssueKind.CancelledFile, IssueSeverity.Ignored, path,
            $"Restauration interrompue par l'utilisateur pendant ce fichier ({Format.Size(size)}) : fichier existant inchangé, copie partielle supprimée.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(PathUtil.ToExtended(path))) File.Delete(PathUtil.ToExtended(path));
        }
        catch
        {
            // best effort
        }
    }

    private void Issue(IssueKind kind, IssueSeverity severity, string path, string message)
    {
        var issue = new CopyIssue(DateTime.Now, kind, severity, path, message);
        _result.IssueCounts[kind] = _result.Count(kind) + 1;
        // Recent events: problems, replaced / kept files and per-element outcomes; never each file restored normally.
        _progress.AddRecent(issue);
        _log?.Write(issue);
    }
}
