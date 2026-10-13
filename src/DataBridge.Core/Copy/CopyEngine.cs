using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DataBridge.Core.Detection;
using DataBridge.Core.IO;
using DataBridge.Core.Logging;
using DataBridge.Core.Native;
using DataBridge.Core.Selection;

namespace DataBridge.Core.Copy;

/// <summary>
/// Streams the copy job by job: folders are walked one directory at a time (never the whole tree in memory),
/// each file is copied with CopyFileEx (attributes and dates preserved). Non-critical errors are logged and
/// skipped; a full or disconnected destination stops the copy cleanly. Cancellation takes effect between files
/// (a very large file in progress is aborted and its partial copy removed by Windows).
/// </summary>
public sealed class CopyEngine
{
    private const long AbortCurrentFileThreshold = 256L * 1024 * 1024;
    private const int ERROR_FILE_NOT_FOUND = 2;
    private const int ERROR_PATH_NOT_FOUND = 3;
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_CRC = 23;
    private const int ERROR_WRITE_FAULT = 29;
    private const int ERROR_READ_FAULT = 30;
    private const int ERROR_SHARING_VIOLATION = 32;
    private const int ERROR_LOCK_VIOLATION = 33;
    private const int ERROR_HANDLE_DISK_FULL = 39;
    private const int ERROR_DISK_FULL = 112;
    private const int ERROR_FILENAME_EXCED_RANGE = 206;
    private const int ERROR_FILE_TOO_LARGE = 223;
    private const int ERROR_REQUEST_ABORTED = 1235;

    private readonly CopyPlan _plan;
    private readonly CopyProgress _progress;
    private readonly NativeMethods.CopyProgressRoutine _callback;
    private CancellationToken _ct;
    private CopyLogWriter? _log;
    private CopyResult _result = null!;
    private string _backupRoot = "";
    private bool _abortedCurrent;
    private string? _fatal;

    public CopyEngine(CopyPlan plan, CopyProgress progress)
    {
        _plan = plan;
        _progress = progress;
        _callback = OnCopyProgress; // kept in a field: the delegate must outlive every native call
    }

    public Task<CopyResult> RunAsync(CancellationToken ct) =>
        Task.Factory.StartNew(() => Run(ct), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private bool StopRequested => _fatal != null || _ct.IsCancellationRequested;

    private CopyResult Run(CancellationToken ct)
    {
        _ct = ct;
        var watch = Stopwatch.StartNew();
        _backupRoot = _plan.BackupRoot;
        _result = new CopyResult { BackupPath = _backupRoot, StartedAt = DateTime.Now, SkipCloudOnly = _plan.SkipCloudOnly };

        // Last line of defence (the UI blocks this before): never copy a source into itself.
        if (DestinationGuard.Check(_plan) is { } conflict)
        {
            _result.Outcome = CopyOutcome.Failed;
            _result.FatalMessage = $"{DestinationConflict.Title}. {DestinationConflict.Message} {conflict.Detail}";
            _result.Duration = watch.Elapsed;
            _progress.Phase = "Terminé";
            return _result;
        }

        // Exact count of what is planned, read from the disk now: progress denominators and final check.
        _progress.Phase = "Analyse";
        try
        {
            var totals = PlanCalculator.CountNow(_plan, ct, n => Interlocked.Exchange(ref _progress.CountedFiles, n));
            var forecast = totals.Sum.Forecast(_plan.SkipCloudOnly);
            _result.Forecast = forecast;
            _progress.SelectedFiles = forecast.SelectedFileCount;
            _progress.PlannedFiles = forecast.PlannedCopyFileCount;
            _progress.PlannedBytes = forecast.PlannedCopyBytes;
            _progress.PlannedCloudSkipped = forecast.SkippedCloudOnlyCount;
        }
        catch (OperationCanceledException)
        {
            // Cancelled before the first file: the (empty) backup folder still gets its log below.
        }
        catch (Exception ex)
        {
            _fatal = "Analyse de la sélection impossible : " + ex.Message;
        }
        _progress.Counting = false;

        try
        {
            Directory.CreateDirectory(_backupRoot);
        }
        catch (Exception ex)
        {
            _result.Outcome = CopyOutcome.Failed;
            _result.FatalMessage = "Impossible de créer le dossier de sauvegarde : " + ex.Message;
            _result.Duration = watch.Elapsed;
            return _result;
        }

        _log = new CopyLogWriter(_backupRoot);
        _progress.Phase = "Copie";
        _progress.WifiTotal = _plan.Wifi?.Profiles.Count ?? 0;

        try
        {
            foreach (var job in _plan.Jobs)
            {
                if (StopRequested) break;
                var jr = new JobResult(job, Path.Combine(_backupRoot, job.DestinationRelative)) { Started = true };
                _result.Jobs.Add(jr);
                _progress.Category = job.Category;
                _progress.Item = job.Label;
                if (job.IsFile) RunFileJob(job, jr);
                else RunFolderJob(job, jr);
            }

            if (!StopRequested && _plan.Wifi is { } wifi)
                RunWifi(wifi);
            if (!StopRequested && _plan.NetworkDrives.Count > 0)
                RunNetworkDrives(_plan.NetworkDrives);
            if (!StopRequested && _plan.Printers is { } printers)
                RunPrinters(printers);
            if (!StopRequested && _plan.Outlook is { } mailbox)
                RunOutlook(mailbox);
        }
        catch (Exception ex)
        {
            _fatal ??= "Erreur inattendue : " + ex.Message;
        }

        _result.Outcome = _fatal != null ? CopyOutcome.Failed : ct.IsCancellationRequested ? CopyOutcome.Cancelled : CopyOutcome.Completed;
        _result.FatalMessage = _fatal;
        _result.CopiedFileCount = Interlocked.Read(ref _progress.CopiedFiles);
        _result.CopiedBytes = Interlocked.Read(ref _progress.CopiedBytes);
        _result.SkippedCloudOnlyCount = _progress.CloudSkipped;
        _result.SkippedCloudOnlyBytes = _progress.CloudSkippedBytes;
        _result.SkippedFileCount = _progress.SkippedFiles;
        _result.ErrorFileCount = _progress.ErrorFiles;
        _result.ErrorFolderCount = _progress.ErrorFolders;
        _result.InterruptedFileCount = _progress.InterruptedFiles;
        _result.LockedFileCount = _progress.Locked;
        _result.DuplicateCount = _progress.Duplicates;
        _result.CopiedWhileOpenCount = _result.Count(IssueKind.CopiedWhileOpen);
        _result.Duration = watch.Elapsed;

        _progress.Phase = "Journal";
        _progress.CurrentFile = "";
        try
        {
            // 2.0: structured description of the backup, read by the restore (metadata only, never a secret).
            _result.ManifestPath = Restore.BackupManifestWriter.Write(_backupRoot, Restore.BackupManifestWriter.Build(_plan, _result, DateTime.Now));
        }
        catch (Exception ex)
        {
            _result.ManifestError = ex.Message;
        }
        try
        {
            _result.LogPath = _log.Complete(_plan, _result);
        }
        catch (Exception ex)
        {
            _result.FatalMessage ??= "Journal non écrit : " + ex.Message;
        }
        _progress.Phase = "Terminé";
        return _result;
    }

    // ------------------------------------------------------------------ jobs

    /// <param name="Broken">The destination folder could not be created: files beneath are counted as errors.</param>
    private readonly record struct Frame(string Source, string Destination, bool Included, Coverage Coverage, bool Broken);

    private const string BrokenDestinationMessage = "Dossier de destination non créé : fichier non copié.";

    private void RunFolderJob(CopyJob job, JobResult jr)
    {
        var selection = job.Selection;
        var coverage = job.Coverage;
        var stack = new Stack<Frame>();
        stack.Push(new Frame(job.SourcePath, jr.DestinationFull,
            selection?.IsIncluded(job.SourcePath) ?? true,
            coverage?.Of(job.SourcePath, true) ?? Coverage.None, false));

        var subdirs = new List<Frame>();
        while (stack.Count > 0 && !StopRequested)
        {
            var frame = stack.Pop();
            if (frame.Coverage == Coverage.Full) continue;
            var rulesBelow = selection?.HasRulesBelow(frame.Source) ?? false;
            if (!frame.Included && !rulesBelow) continue;

            var broken = frame.Broken;
            var destinationReady = false;
            if (frame.Included && !broken)
            {
                // Included folders are recreated even when empty.
                if (EnsureDirectory(frame.Destination, jr)) destinationReady = true;
                else if (StopRequested) break;
                else broken = true;
            }

            subdirs.Clear();
            using var enumerator = SafeEnumerate(frame.Source, jr).GetEnumerator();
            while (!StopRequested && enumerator.MoveNext())
            {
                var e = enumerator.Current;
                var included = selection?.IsChildIncluded(e.FullPath, frame.Included) ?? frame.Included;
                var cov = frame.Coverage == Coverage.None ? Coverage.None : coverage!.Of(e.FullPath, e.IsDirectory);

                if (e.IsLink)
                {
                    if (included && cov != Coverage.Full) Skipped(e.FullPath, jr);
                    continue;
                }

                if (e.IsDirectory)
                {
                    subdirs.Add(new Frame(e.FullPath, Path.Combine(frame.Destination, e.Name), included, cov, false));
                    continue;
                }

                if (!included) continue;
                if (cov == Coverage.Full)
                {
                    _progress.Duplicates++;
                    jr.Duplicates++;
                    continue;
                }
                if (e.IsCloudOnly && _plan.SkipCloudOnly)
                {
                    CloudSkipped(e.FullPath, e.Size, jr);
                    continue;
                }

                if (!destinationReady && !broken)
                {
                    if (EnsureDirectory(frame.Destination, jr)) destinationReady = true;
                    else if (StopRequested) break;
                    else broken = true;
                }
                if (broken)
                {
                    FileError(IssueKind.WriteError, e.FullPath, BrokenDestinationMessage, e.Size, jr);
                    continue;
                }
                CopyFile(e.FullPath, Path.Combine(frame.Destination, e.Name), e.Size, jr);
            }

            for (var i = subdirs.Count - 1; i >= 0; i--) stack.Push(subdirs[i] with { Broken = broken });
        }
    }

    private void RunFileJob(CopyJob job, JobResult jr)
    {
        if (job.Coverage?.Of(job.SourcePath, false) == Coverage.Full)
        {
            _progress.Duplicates++;
            jr.Duplicates++;
            return;
        }
        var entry = FastDirectory.GetEntry(job.SourcePath);
        if (entry is null)
        {
            FileError(IssueKind.NotFound, job.SourcePath, "Fichier introuvable au moment de la copie.", 0, jr);
            return;
        }
        if (entry.IsCloudOnly && _plan.SkipCloudOnly)
        {
            CloudSkipped(entry.FullPath, entry.Size, jr);
            return;
        }
        var destination = jr.DestinationFull;
        if (!EnsureDirectory(Path.GetDirectoryName(destination)!, jr))
        {
            if (!StopRequested) FileError(IssueKind.WriteError, job.SourcePath, BrokenDestinationMessage, entry.Size, jr);
            return;
        }
        CopyFile(job.SourcePath, destination, entry.Size, jr);
    }

    private void RunWifi(WifiJob wifi)
    {
        _progress.Category = Categories.Network;
        _progress.Item = "Profils Wi-Fi";
        var folder = Path.Combine(_backupRoot, wifi.DestinationRelative);
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            foreach (var p in wifi.Profiles)
                _result.Wifi.Add(new WifiExportResult(p.Name, false, null, WifiKeyStatus.Unknown, ex.Message));
            return;
        }

        foreach (var profile in wifi.Profiles)
        {
            if (StopRequested) break;
            _progress.CurrentFile = "Profil Wi-Fi : " + profile.Name;
            var r = WifiService.Export(profile, folder, wifi.RecoveredXml?.GetValueOrDefault(profile.Name));
            _result.Wifi.Add(r);
            _progress.WifiDone++;
        }
    }

    /// <summary>Network drives: letter, share and account name are recorded in the manifest and the log (no file, no password).</summary>
    private void RunNetworkDrives(IReadOnlyList<NetworkDriveInfo> drives)
    {
        _progress.Category = Categories.Network;
        _progress.Item = "Lecteurs réseau";
        _result.NetworkDrives.AddRange(drives);
    }

    /// <summary>Printers → Imprimantes\Imprimantes.prnbak (read-only export, session rights). A failure is reported, the backup goes on.</summary>
    private void RunPrinters(PrinterTransfer.PrinterBackupJob job)
    {
        _progress.Phase = "Imprimantes";
        _progress.Category = Categories.Printers;
        _progress.Item = "Imprimantes";
        _progress.ExtraPhase = true;
        _progress.ExtraDone = 0;
        _progress.ExtraTotal = 0;
        var r = PrinterTransfer.PrinterTransferService.Backup(job, _backupRoot, s => _progress.CurrentFile = s, _ct);
        _result.Printers = r;
        if (r.Error != null)
            Issue(IssueKind.Other, IssueSeverity.Error, Path.Combine(_backupRoot, PrinterTransfer.PrinterBackupJob.DestinationRelative),
                "Export des imprimantes impossible : " + r.Error);
        else if (r.ArchivePath != null)
            Issue(IssueKind.Other, IssueSeverity.Info, r.ArchivePath, $"{Format.Plural(r.Printers.Count, "imprimante exportée", "imprimantes exportées")} ({Format.Size(r.ArchiveSize)}).");
    }

    /// <summary>Outlook mailbox → new PST in Outlook\Boite (everything of the checked categories). A failure is reported, the backup goes on.</summary>
    private void RunOutlook(Outlook.OutlookBackupJob job)
    {
        _progress.Phase = "Outlook";
        _progress.Category = Categories.Outlook;
        _progress.Item = "Boîte aux lettres " + job.StoreName;
        _progress.ExtraPhase = true;
        _progress.ExtraDone = 0;
        _progress.ExtraTotal = 0;
        var r = global::DataBridge.Core.Outlook.OutlookTransfer.Backup(job, _backupRoot, s => _progress.CurrentFile = s, (done, total) =>
        {
            _progress.ExtraDone = done;
            _progress.ExtraTotal = total;
        }, _ct);
        _result.Outlook = r;
        if (!r.Ok && !r.Cancelled)
            Issue(IssueKind.Other, IssueSeverity.Error, "Outlook — " + job.StoreName, r.Headline);
    }

    // ------------------------------------------------------------------ file operations

    private IEnumerable<FsEntry> SafeEnumerate(string directory, JobResult jr)
    {
        IEnumerator<FsEntry> inner;
        try
        {
            inner = FastDirectory.Enumerate(directory).GetEnumerator();
        }
        catch (FsException ex)
        {
            ReportFailure(ex.Win32Error, directory, jr, isDirectory: true);
            yield break;
        }

        using (inner)
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = inner.MoveNext();
                }
                catch (FsException ex)
                {
                    ReportFailure(ex.Win32Error, directory, jr, isDirectory: true);
                    yield break;
                }
                if (!moved) yield break;
                yield return inner.Current;
            }
        }
    }

    private bool EnsureDirectory(string path, JobResult jr)
    {
        try
        {
            Directory.CreateDirectory(PathUtil.ToExtended(path));
            return true;
        }
        catch (Exception ex)
        {
            var code = ex.HResult & 0xFFFF;
            if (code is ERROR_DISK_FULL or ERROR_HANDLE_DISK_FULL)
                Fatal("Espace insuffisant sur la destination.");
            else if (!DestinationAlive())
                Fatal("La destination n'est plus accessible (support débranché ?).");
            else
                FolderError(IssueKind.WriteError, path, "Création du dossier impossible : " + ex.Message, jr);
            return false;
        }
    }

    private void CopyFile(string source, string destination, long size, JobResult jr)
    {
        _progress.CurrentFile = source;
        Interlocked.Exchange(ref _progress.CurrentFileBytes, 0);
        _abortedCurrent = false;

        var cancel = 0;
        var ok = NativeMethods.CopyFileExW(PathUtil.ToExtended(source), PathUtil.ToExtended(destination), _callback, IntPtr.Zero,
            ref cancel, NativeMethods.COPY_FILE_FAIL_IF_EXISTS | NativeMethods.COPY_FILE_ALLOW_DECRYPTED_DESTINATION);
        var error = ok ? 0 : Marshal.GetLastWin32Error();
        Interlocked.Exchange(ref _progress.CurrentFileBytes, 0);

        if (ok)
        {
            Completed(size, jr);
            return;
        }

        if (error == ERROR_REQUEST_ABORTED && _abortedCurrent)
        {
            Interrupted(source, size, jr);
            return;
        }

        if (error is ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION)
        {
            // The file is open in another program. Retry with full read/write sharing, which succeeds when the
            // other program allows it (never forcing a lock, never using elevated backup privileges).
            if (TrySharedCopy(source, destination, size, out var sharedError))
            {
                Completed(size, jr);
                Issue(IssueKind.CopiedWhileOpen, IssueSeverity.Info, source,
                    "Fichier ouvert dans un autre programme : copié en lecture partagée (contenu tel qu'au moment de la copie).");
                return;
            }
            if (sharedError == -1)
            {
                Interrupted(source, size, jr);
                return;
            }
            error = sharedError != 0 ? sharedError : error;
        }

        ReportFailure(error, source, jr, isDirectory: false, size);
    }

    private void Completed(long size, JobResult jr)
    {
        Interlocked.Increment(ref _progress.CopiedFiles);
        Interlocked.Add(ref _progress.CopiedBytes, size);
        jr.CopiedFiles++;
        jr.CopiedBytes += size;
    }

    private bool TrySharedCopy(string source, string destination, long size, out int error)
    {
        error = 0;
        var created = false;
        try
        {
            using (var input = new FileStream(PathUtil.ToExtended(source), FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan))
            using (var output = new FileStream(PathUtil.ToExtended(destination), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                created = true;
                var buffer = new byte[1 << 20];
                long copied = 0;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (_ct.IsCancellationRequested && size - copied > AbortCurrentFileThreshold)
                    {
                        output.Dispose();
                        File.Delete(PathUtil.ToExtended(destination));
                        error = -1;
                        return false;
                    }
                    output.Write(buffer, 0, read);
                    copied += read;
                    Interlocked.Exchange(ref _progress.CurrentFileBytes, copied);
                }
            }
            Interlocked.Exchange(ref _progress.CurrentFileBytes, 0);

            var info = new FileInfo(PathUtil.ToExtended(source));
            var target = new FileInfo(PathUtil.ToExtended(destination));
            target.CreationTimeUtc = info.CreationTimeUtc;
            target.LastWriteTimeUtc = info.LastWriteTimeUtc;
            return true;
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _progress.CurrentFileBytes, 0);
            error = ex.HResult & 0xFFFF;
            if (created)
            {
                try { File.Delete(PathUtil.ToExtended(destination)); } catch { /* best effort */ }
            }
            return false;
        }
    }

    private uint OnCopyProgress(long totalFileSize, long totalBytesTransferred, long streamSize, long streamBytesTransferred,
        uint streamNumber, uint callbackReason, IntPtr sourceFile, IntPtr destinationFile, IntPtr data)
    {
        Interlocked.Exchange(ref _progress.CurrentFileBytes, totalBytesTransferred);
        if (_ct.IsCancellationRequested && totalFileSize - totalBytesTransferred > AbortCurrentFileThreshold)
        {
            _abortedCurrent = true;
            return NativeMethods.PROGRESS_CANCEL; // Windows deletes the partial destination file
        }
        return NativeMethods.PROGRESS_CONTINUE;
    }

    private void ReportFailure(int error, string path, JobResult jr, bool isDirectory, long size = 0)
    {
        void Fail(IssueKind kind, string message)
        {
            if (isDirectory) FolderError(kind, path, message, jr);
            else FileError(kind, path, message, size, jr);
        }

        if (error is ERROR_DISK_FULL or ERROR_HANDLE_DISK_FULL)
        {
            Fatal("Espace insuffisant sur la destination.");
            Fail(IssueKind.WriteError, "Disque de destination plein.");
            return;
        }

        // Whatever the code (not ready, device gone, path not found...), a vanished backup folder is critical.
        if (!DestinationAlive())
        {
            Fatal("La destination n'est plus accessible (support débranché ?).");
            Fail(IssueKind.WriteError, "Destination inaccessible.");
            return;
        }

        var what = isDirectory ? "Dossier" : "Fichier";
        var (kind, message) = error switch
        {
            ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION => (IssueKind.Locked, $"{what} verrouillé par un autre programme."),
            ERROR_ACCESS_DENIED => (IssueKind.AccessDenied, $"{what} non accessible avec les droits de la session."),
            ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND => (IssueKind.NotFound, $"{what} disparu ou déplacé pendant la copie."),
            ERROR_FILENAME_EXCED_RANGE => (IssueKind.PathTooLong, "Chemin ou nom trop long."),
            ERROR_CRC or ERROR_READ_FAULT or 1117 => (IssueKind.ReadError, "Erreur de lecture du support source."),
            ERROR_WRITE_FAULT => (IssueKind.WriteError, "Erreur d'écriture sur la destination."),
            ERROR_FILE_TOO_LARGE => (IssueKind.WriteError, "Fichier trop volumineux pour le système de fichiers de destination (FAT32 : 4 Go max)."),
            >= 362 and <= 404 => (IssueKind.CloudOnly, "Fichier cloud non disponible (fournisseur OneDrive/cloud indisponible)."),
            _ => (IssueKind.Other, new Win32Exception(error).Message),
        };
        Fail(kind, message);
    }

    private bool DestinationAlive()
    {
        try
        {
            return Directory.Exists(_backupRoot);
        }
        catch
        {
            return false;
        }
    }

    private void Fatal(string message) => _fatal ??= message;

    // ------------------------------------------------------------------ counters (one place per counter)

    /// <summary>A planned file that could not be copied.</summary>
    private void FileError(IssueKind kind, string path, string message, long size, JobResult jr)
    {
        _progress.ErrorFiles++;
        Interlocked.Add(ref _progress.FailedBytes, size);
        jr.ErrorFiles++;
        if (kind == IssueKind.Locked) _progress.Locked++;
        Issue(kind, IssueSeverity.Error, path, message);
    }

    /// <summary>A folder that could not be read or created (its files are unknown, or reported one by one).</summary>
    private void FolderError(IssueKind kind, string path, string message, JobResult jr)
    {
        _progress.ErrorFolders++;
        jr.ErrorFolders++;
        Issue(kind, IssueSeverity.Error, path, message);
    }

    /// <summary>Cloud-only file left online on purpose: selected, never planned nor copied.</summary>
    private void CloudSkipped(string path, long size, JobResult jr)
    {
        _progress.CloudSkipped++;
        _progress.CloudSkippedBytes += size;
        jr.CloudSkipped++;
        Issue(IssueKind.CloudOnly, IssueSeverity.Ignored, path, $"Disponible uniquement en ligne ({Format.Size(size)}) : non téléchargé.");
    }

    /// <summary>Symbolic link / junction: never followed (duplicates, loops).</summary>
    private void Skipped(string path, JobResult jr)
    {
        _progress.SkippedFiles++;
        jr.Skipped++;
        Issue(IssueKind.Link, IssueSeverity.Ignored, path, "Lien symbolique / jonction : non suivi pour éviter les doublons et les boucles.");
    }

    /// <summary>Large file aborted by the cancellation (Windows removes the partial copy).</summary>
    private void Interrupted(string path, long size, JobResult jr)
    {
        _progress.InterruptedFiles++;
        Interlocked.Add(ref _progress.FailedBytes, size);
        jr.Interrupted++;
        Issue(IssueKind.CancelledFile, IssueSeverity.Ignored, path,
            $"Copie interrompue par l'utilisateur pendant ce fichier ({Format.Size(size)}) : copie partielle supprimée.");
    }

    private void Issue(IssueKind kind, IssueSeverity severity, string path, string message)
    {
        var issue = new CopyIssue(DateTime.Now, kind, severity, path, message);
        _result.IssueCounts[kind] = _result.Count(kind) + 1;
        if (severity != IssueSeverity.Info || kind == IssueKind.CopiedWhileOpen) _progress.AddRecent(issue);
        _log?.Write(issue);
    }
}
