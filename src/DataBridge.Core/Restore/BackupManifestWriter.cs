using System.Text;
using System.Text.Json;
using DataBridge.Core.Copy;
using DataBridge.Core.Detection;
using DataBridge.Core.Profile;
using DataBridge.Core.Selection;

namespace DataBridge.Core.Restore;

/// <summary>What the backup selection knew about each copy job, kept with the plan to write the manifest afterwards.</summary>
public sealed class ManifestSeed
{
    public List<(PersonalFolderInfo Info, CopyJob Job, bool Partial)> Personal { get; } = [];
    public List<(AdditionalItem Item, CopyJob Job)> Additional { get; } = [];
    public (SignatureInfo Info, CopyJob Job)? Signatures { get; set; }
    public List<(BrowserInfo Info, IReadOnlyList<BrowserProfileInfo> Profiles, IReadOnlyList<CopyJob> Jobs)> Browsers { get; } = [];
    public (WallpaperInfo Info, CopyJob Job)? Wallpaper { get; set; }
}

/// <summary>
/// Writes databridge-manifest.json at the end of a backup, from the plan and the copy result (the same figures as
/// copy-log.txt). Only metadata: names, paths, counts, sizes. Never a key, password, token, cookie or file content.
/// </summary>
public static class BackupManifestWriter
{
    public static BackupManifest Build(CopyPlan plan, CopyResult result, DateTime completedAt)
    {
        var seed = plan.ManifestSeed ?? new ManifestSeed();
        var m = new BackupManifest
        {
            DataBridgeVersion = AppInfo.Version,
            BackupId = plan.BackupId,
            CreatedAt = new DateTimeOffset(result.StartedAt),
            CompletedAt = new DateTimeOffset(completedAt),
            Status = result.Outcome switch { CopyOutcome.Completed => "completed", CopyOutcome.Cancelled => "cancelled", _ => "failed" },
            Source = new ManifestSource
            {
                Computer = plan.Profile.MachineName,
                User = plan.Profile.UserName,
                Domain = plan.Profile.UserDomain,
                ProfilePath = plan.Profile.ProfilePath,
                WindowsVersion = plan.Profile.WindowsVersion,
            },
            Totals = new ManifestTotals { Files = result.CopiedFileCount, Bytes = result.CopiedBytes },
            Options = new ManifestOptions { SkipCloudOnly = plan.SkipCloudOnly },
        };

        JobResult? Of(CopyJob job) => result.Jobs.FirstOrDefault(j => ReferenceEquals(j.Job, job));
        bool Copied(CopyJob job) => Of(job) is { Started: true };

        foreach (var (info, job, partial) in seed.Personal.Where(p => Copied(p.Job)))
        {
            var jr = Of(job)!;
            m.PersonalFolders.Add(new ManifestPersonalFolder
            {
                Kind = info.Definition.Kind.ToString(),
                Label = info.Definition.DisplayName,
                OriginalPath = job.SourcePath,
                RelativePath = job.DestinationRelative,
                Location = info.Location switch { FolderLocation.OneDrive => "oneDrive", FolderLocation.Network => "network", _ => "local" },
                OneDrive = info.Location == FolderLocation.OneDrive
                    ? new ManifestOneDrive
                    {
                        AccountType = info.OneDriveAccount?.TypeLabel ?? "Inconnu",
                        Organization = info.OneDriveAccount?.TenantDisplayName,
                    }
                    : null,
                Partial = partial,
                Files = jr.CopiedFiles,
                Bytes = jr.CopiedBytes,
            });
        }

        foreach (var (item, job) in seed.Additional.Where(a => Copied(a.Job)))
        {
            var jr = Of(job)!;
            m.AdditionalItems.Add(new ManifestAdditionalItem
            {
                Name = item.Name,
                OriginalPath = item.Path,
                RelativePath = job.DestinationRelative,
                IsFile = !item.IsDirectory,
                Files = jr.CopiedFiles,
                Bytes = jr.CopiedBytes,
            });
        }

        if (seed.Signatures is { } sig && Copied(sig.Job))
        {
            var jr = Of(sig.Job)!;
            m.OutlookSignatures = new ManifestSignatures
            {
                OriginalPath = sig.Info.Path,
                RelativePath = sig.Job.DestinationRelative,
                Names = sig.Info.Names.ToList(),
                Files = jr.CopiedFiles,
                Bytes = jr.CopiedBytes,
            };
        }

        foreach (var (info, profiles, jobs) in seed.Browsers.Where(b => b.Jobs.Any(Copied)))
        {
            var main = jobs.FirstOrDefault(j => PathUtilAreSame(j.SourcePath, info.DataRoot));
            var browser = new ManifestBrowser
            {
                Browser = info.Kind.ToString().ToLowerInvariant(),
                Name = info.Name,
                OriginalDataRoot = info.DataRoot,
                RelativePath = main?.DestinationRelative ?? "",
                Files = jobs.Sum(j => Of(j)?.CopiedFiles ?? 0),
                Bytes = jobs.Sum(j => Of(j)?.CopiedBytes ?? 0),
            };
            foreach (var p in profiles)
            {
                // Profile inside the data root: same relative layout under the browser folder; external profile: its own job.
                var external = jobs.FirstOrDefault(j => PathUtilAreSame(j.SourcePath, p.FullPath));
                string relative;
                if (external != null) relative = external.DestinationRelative;
                else if (main != null && IO.PathUtil.IsStrictlyUnder(p.FullPath, info.DataRoot))
                    relative = Path.Combine(main.DestinationRelative, IO.PathUtil.Relative(p.FullPath, info.DataRoot));
                else continue;
                browser.Profiles.Add(new ManifestBrowserProfile
                {
                    Folder = p.FolderName,
                    DisplayName = p.DisplayName,
                    Account = p.Account,
                    IsActive = p.IsActive,
                    OriginalPath = p.FullPath,
                    RelativePath = relative,
                });
            }
            m.Browsers.Add(browser);
        }

        if (plan.Wifi is { } wifi)
        {
            foreach (var w in result.Wifi.Where(w => w.Exported && w.FileName != null))
            {
                var profile = wifi.Profiles.FirstOrDefault(p => p.Name == w.ProfileName);
                m.Wifi.Add(new ManifestWifiProfile
                {
                    Name = w.ProfileName,
                    File = Path.Combine(wifi.DestinationRelative, w.FileName!),
                    Authentication = profile?.Authentication,
                    KeyIncluded = w.KeyIncluded,
                    Scope = profile is null ? null : profile.IsPerUser ? "currentUser" : "allUsers",
                });
            }
        }

        foreach (var d in result.NetworkDrives)
            m.NetworkDrives.Add(new ManifestNetworkDrive { Letter = d.Letter, RemotePath = d.RemotePath, UserName = d.UserName, Persistent = d.Persistent });

        if (seed.Wallpaper is { } wp && Of(wp.Job) is { CopiedFiles: > 0 } wjr)
        {
            m.Wallpaper = new ManifestWallpaper
            {
                OriginalPath = wp.Info.Path,
                RelativePath = wp.Job.DestinationRelative,
                Bytes = wjr.CopiedBytes,
            };
        }

        string? Relative(string? path) => path != null && IO.PathUtil.IsStrictlyUnder(IO.PathUtil.Normalize(path), IO.PathUtil.Normalize(plan.BackupRoot))
            ? IO.PathUtil.Relative(IO.PathUtil.Normalize(path), IO.PathUtil.Normalize(plan.BackupRoot))
            : null;

        if (result.Printers is { ArchivePath: { } archive } printers && Relative(archive) is { } archiveRelative)
        {
            m.Printers = new ManifestPrinters
            {
                File = archiveRelative,
                Bytes = printers.ArchiveSize,
                Printers = printers.Printers.Select(p => new ManifestPrinter
                {
                    Name = p.Name,
                    Kind = p.Kind.ToString(),
                    Supported = p.Support == DataBridge.Printers.Model.SupportLevel.Supported,
                    IsDefault = p.IsDefault,
                }).ToList(),
            };
        }

        if (plan.Outlook is { } mailbox && result.Outlook is { } outlook && (outlook.PstPath != null || outlook.AutocompleteFile != null))
        {
            m.OutlookMailbox = new ManifestOutlookMailbox
            {
                MailboxName = mailbox.StoreName,
                Categories = Outlook.OutlookCategoryText.ToNames(mailbox.Categories),
                RelativePath = Outlook.OutlookBackupJob.DestinationRelative,
                PstFile = Relative(outlook.PstPath),
                AutocompleteFile = Relative(outlook.AutocompleteFile),
                Mails = outlook.Mails,
                Contacts = outlook.Contacts,
                Events = outlook.Events,
                Status = outlook.Cancelled ? "cancelled" : outlook.Error != null ? "failed" : outlook.Ok ? "completed" : "partial",
            };
        }

        if (m.PersonalFolders.Count > 0) m.Categories.Add("personalFolders");
        if (m.AdditionalItems.Count > 0) m.Categories.Add("additionalItems");
        if (m.OutlookSignatures != null) m.Categories.Add("outlookSignatures");
        if (m.Browsers.Count > 0) m.Categories.Add("browsers");
        if (m.Wifi.Count > 0) m.Categories.Add("wifi");
        if (m.NetworkDrives.Count > 0) m.Categories.Add("networkDrives");
        if (m.Wallpaper != null) m.Categories.Add("wallpaper");
        if (m.Printers != null) m.Categories.Add("printers");
        if (m.OutlookMailbox != null) m.Categories.Add("outlookMailbox");
        return m;
    }

    private static bool PathUtilAreSame(string a, string b) => IO.PathUtil.AreSame(a, b);

    /// <summary>Writes the manifest (UTF-8 without BOM) at the root of the backup and returns its path.</summary>
    public static string Write(string backupRoot, BackupManifest manifest)
    {
        var path = Path.Combine(backupRoot, BackupManifest.FileName);
        var json = JsonSerializer.Serialize(manifest, BackupManifest.JsonOptions);
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return path;
    }
}
