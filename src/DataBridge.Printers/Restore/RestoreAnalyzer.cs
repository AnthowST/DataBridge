using DataBridge.Printers.Archive;
using DataBridge.Printers.Inventory;
using DataBridge.Printers.Model;

namespace DataBridge.Printers.Restore;

/// <summary>Compares the archive with the target PC and proposes, for each printer, actions that never overwrite anything silently.</summary>
public static class RestoreAnalyzer
{
    public static RestorePlan Analyze(OpenedArchive archive)
    {
        if (!archive.IsUsable || archive.Inventory == null || archive.Manifest == null)
            throw new InvalidOperationException("Archive inutilisable.");
        var inv = archive.Inventory;
        bool sessionUser = SystemInfo.RunsAsSessionUser(out var processUser, out var sessionUserName);
        var plan = new RestorePlan
        {
            Archive = archive,
            Target = SystemInfo.Collect(),
            SpoolerRunning = SystemInfo.SpoolerRunning(),
            RunsAsSessionUser = sessionUser,
            ProcessUser = processUser,
            SessionUser = sessionUserName,
        };
        var src = archive.Manifest.Source;
        string env = SystemInfo.NativeEnvironment();

        if (plan.Target.Architecture != src.Architecture)
            plan.GlobalIssues.Add(new(IssueLevel.Warning, $"Architecture différente : source {src.Architecture}, ce PC {plan.Target.Architecture}. Les pilotes exportés ne sont utilisables que s'ils contiennent la bonne architecture."));
        if (plan.Target.OsBuild < 22000)
            plan.GlobalIssues.Add(new(IssueLevel.Warning, $"Ce PC n'est pas sous Windows 11 (build {plan.Target.OsBuild}) : la restauration des imprimantes est validée pour Windows 11."));
        if (env != "Windows x64")
            plan.GlobalIssues.Add(new(IssueLevel.Blocking, $"Ce PC est en {env} : seule la restauration vers Windows x64 est prise en charge."));
        if (!plan.SpoolerRunning)
            plan.GlobalIssues.Add(new(IssueLevel.Warning, "Le service Spouleur d'impression est arrêté : il sera démarré (droits administrateur) avant la restauration."));
        if (!sessionUser)
            plan.GlobalIssues.Add(new(IssueLevel.Warning, $"DataBridge s'exécute sous le compte « {processUser} » alors que la session ouverte est celle de « {sessionUserName} ». Les connexions partagées, préférences et l'imprimante par défaut iraient dans le mauvais profil : elles sont désactivées. Relancez PrintBridge normalement (sans « Exécuter en tant qu'autre utilisateur »)."));
        if (plan.Target.JoinStatus != src.JoinStatus || !string.Equals(plan.Target.DomainOrWorkgroup, src.DomainOrWorkgroup, StringComparison.OrdinalIgnoreCase))
            plan.GlobalIssues.Add(new(IssueLevel.Info, $"Source : {src.JoinStatus} « {src.DomainOrWorkgroup} », ce PC : {plan.Target.JoinStatus} « {plan.Target.DomainOrWorkgroup} ». Les permissions qui citent des comptes de domaine ou locaux peuvent ne pas se résoudre."));

        // Current state of the target (lightweight: no settings).
        LiveInventory target = plan.SpoolerRunning ? PrinterInventory.Read(details: false) : new LiveInventory { Environment = env };

        foreach (var entry in inv.Printers)
        {
            var port = entry.PortId == null ? null : inv.Ports.FirstOrDefault(p => p.Id == entry.PortId);
            var driver = entry.DriverId == null ? null : inv.Drivers.FirstOrDefault(d => d.Id == entry.DriverId);
            var p = new PrinterPlan { Entry = entry, Port = port, Driver = driver };
            AnalyzePrinter(p, archive, target, env, sessionUser);
            p.MakeDefault = string.Equals(inv.DefaultPrinter, entry.Name, StringComparison.OrdinalIgnoreCase) && sessionUser;
            p.Selected = p.CanRestore;
            plan.Printers.Add(p);
        }

        int? currentLegacy = SystemInfo.LegacyDefaultPrinterMode();
        plan.LegacyModeDiffers = inv.LegacyDefaultPrinterMode == 1 && currentLegacy != 1;
        plan.RestoreLegacyDefaultMode = plan.LegacyModeDiffers && sessionUser;
        return plan;
    }

    private static void AnalyzePrinter(PrinterPlan p, OpenedArchive archive, LiveInventory target, string env, bool sessionUser)
    {
        var e = p.Entry;
        if (e.Support != SupportLevel.Supported)
        {
            p.FixedIssues.Add(new(IssueLevel.Blocking, e.SupportNote ?? "Type d'imprimante non pris en charge."));
            p.NameChoices = new() { NameAction.Skip };
            p.NameAction = NameAction.Skip;
            return;
        }
        if (archive.BadFiles.Any(f => f.StartsWith($"printers/{e.Id}/", StringComparison.Ordinal)))
            p.FixedIssues.Add(new(IssueLevel.Warning, "Certains réglages de cette imprimante sont endommagés dans l'archive : ils ne seront pas restaurés."));

        // Existing printer with the same name.
        var existing = target.Printers.FirstOrDefault(x => string.Equals(x.Entry.Name, e.Name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            p.NameConflict = true;
            p.ExistingIsConnection = existing.Entry.Kind is PrinterKind.SharedConnection or PrinterKind.MachineConnection;
            p.ExistingPrinterInfo = p.ExistingIsConnection
                ? "Une connexion portant ce nom existe déjà."
                : $"Une imprimante « {existing.Entry.Name} » existe déjà (pilote {existing.Entry.DriverName}, port {existing.Entry.PortName}).";
            p.NameChoices = p.IsConnection
                ? new() { NameAction.KeepExisting, NameAction.Skip, NameAction.Replace }
                : new() { NameAction.Skip, NameAction.KeepExisting, NameAction.CreateWithOtherName, NameAction.Replace };
            p.NameAction = p.IsConnection ? NameAction.KeepExisting : NameAction.Skip;
            p.NewName = e.Name + " (restaurée)";
        }
        else
        {
            p.NameChoices = new() { NameAction.Create, NameAction.Skip };
            p.NameAction = NameAction.Create;
        }

        if (p.IsConnection)
        {
            if (!sessionUser)
                p.FixedIssues.Add(new(IssueLevel.Blocking, "Connexion par utilisateur : impossible depuis un autre compte que celui de la session."));
            AnalyzeDriver(p, archive, target, env, isConnection: true);
            p.PortChoices = new() { PortAction.NotNeeded };
            p.PortAction = PortAction.NotNeeded;
            return;
        }

        // Port.
        if (p.Port == null || p.Port.Kind != PortKind.StandardTcpIp)
        {
            p.FixedIssues.Add(new(IssueLevel.Blocking, "Configuration du port absente de l'archive."));
        }
        else
        {
            var existingPort = target.Ports.FirstOrDefault(x => string.Equals(x.Name, p.Port.Name, StringComparison.OrdinalIgnoreCase));
            p.PortUsedBy = target.Printers.Where(x => string.Equals(x.Entry.PortName, p.Port.Name, StringComparison.OrdinalIgnoreCase)
                    && !(p.NameConflict && string.Equals(x.Entry.Name, e.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(x => x.Entry.Name).ToList();
            p.NewPortName = UniquePortName(p.Port.Name, target);
            if (existingPort == null)
            {
                p.PortChoices = new() { PortAction.Create, PortAction.CreateWithOtherName };
                p.PortAction = PortAction.Create;
            }
            else if (existingPort.Kind == PortKind.StandardTcpIp && existingPort.SameConfigurationAs(p.Port))
            {
                p.PortChoices = new() { PortAction.ReuseIdentical, PortAction.CreateWithOtherName };
                p.PortAction = PortAction.ReuseIdentical;
            }
            else
            {
                p.PortConflict = true;
                p.ExistingPortInfo = $"le port « {existingPort.Name} » existe avec une autre configuration ({existingPort.Describe()})";
                p.PortChoices = existingPort.Kind == PortKind.StandardTcpIp
                    ? new() { PortAction.CreateWithOtherName, PortAction.KeepExisting, PortAction.Reconfigure }
                    : new() { PortAction.CreateWithOtherName };
                p.PortAction = PortAction.CreateWithOtherName;
            }
        }

        AnalyzeDriver(p, archive, target, env, isConnection: false);

        // Share name used by another printer of the target.
        if (e.IsShared && e.ShareName != null)
        {
            var shareOwner = target.Printers.FirstOrDefault(x => x.Entry.IsShared && string.Equals(x.Entry.ShareName, e.ShareName, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(x.Entry.Name, e.Name, StringComparison.OrdinalIgnoreCase));
            if (shareOwner != null)
            {
                p.FixedIssues.Add(new(IssueLevel.Warning, $"Le nom de partage « {e.ShareName} » est déjà utilisé par « {shareOwner.Entry.Name} » : l'imprimante sera créée sans partage."));
                p.RestoreShare = false;
            }
        }

        if (e.Security != null) p.Security = SecurityPlanner.Build(e.Security, mapLocalAccountsByName: false);
        else p.RestoreSecurity = false;
        p.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(PrinterPlan.MapLocalAccountsByName) && e.Security != null)
                p.Security = SecurityPlanner.Build(e.Security, p.MapLocalAccountsByName);
        };
    }

    private static void AnalyzeDriver(PrinterPlan p, OpenedArchive archive, LiveInventory target, string env, bool isConnection)
    {
        var d = p.Driver;
        string? driverName = p.Entry.DriverName;
        if (driverName == null)
        {
            p.DriverChoices = new() { isConnection ? DriverAction.FromServer : DriverAction.None };
            p.DriverAction = p.DriverChoices[0];
            p.DriverStatusText = isConnection ? "Pilote inconnu : il sera fourni par le serveur d'impression." : "Pilote inconnu.";
            return;
        }
        var installed = target.Drivers.FirstOrDefault(x => string.Equals(x.Name, driverName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Environment, env, StringComparison.OrdinalIgnoreCase));
        p.DriverUsedBy = target.Printers.Where(x => string.Equals(x.Entry.DriverName, driverName, StringComparison.OrdinalIgnoreCase)).Select(x => x.Entry.Name).ToList();
        p.InstalledDriverVersion = installed?.Version;

        p.DriverPackageUsable = d != null && d.Exported && d.PackageFolder != null && d.PackageInf != null
            && archive.FolderIntact(d.PackageFolder)
            && DriverReader.SupportsEnvironment(d.InfArchitectures, env);
        string packageProblem = d == null ? "pilote absent de l'archive"
            : d.IsInbox ? "pilote intégré à Windows"
            : !d.Exported ? $"non exporté ({d.NotExportedReason})"
            : d.PackageFolder != null && !archive.FolderIntact(d.PackageFolder) ? "package endommagé dans l'archive"
            : !DriverReader.SupportsEnvironment(d.InfArchitectures, env) ? $"package sans version {env} ({string.Join("/", d.InfArchitectures)})"
            : "";

        var choices = new List<DriverAction>();
        if (installed != null)
        {
            p.DriverVersionDiffers = d != null && installed.VersionRaw != d.VersionRaw;
            choices.Add(DriverAction.UseInstalled);
            if (p.DriverVersionDiffers && p.DriverPackageUsable) choices.Add(DriverAction.InstallFromArchive);
            choices.Add(DriverAction.InstallUserPackage);
            p.DriverStatusText = p.DriverVersionDiffers
                ? $"Installé en version {installed.Version} (source : {d?.Version})."
                : $"Déjà installé, même version ({installed.Version}).";
        }
        else
        {
            if (p.DriverPackageUsable) choices.Add(DriverAction.InstallFromArchive);
            if (d?.IsInbox == true) choices.Add(DriverAction.InstallInbox);
            if (isConnection) choices.Add(DriverAction.FromServer);
            choices.Add(DriverAction.InstallUserPackage);
            p.DriverStatusText = p.DriverPackageUsable ? $"Non installé : sera installé depuis l'archive (version {d!.Version})."
                : d?.IsInbox == true ? "Non installé : pilote intégré à Windows, sera installé s'il est disponible sur ce PC."
                : isConnection ? $"Non installé ({packageProblem}) : le serveur devra le fournir."
                : $"Non installé et non disponible ({packageProblem}) : sélectionnez un package compatible.";
            if (!p.DriverPackageUsable && d?.IsInbox != true && !isConnection) choices.Insert(0, DriverAction.None);
        }
        p.DriverChoices = choices;
        p.DriverAction = choices[0];
    }

    private static string UniquePortName(string baseName, LiveInventory target)
    {
        for (int i = 1; i < 100; i++)
        {
            string candidate = i == 1 ? baseName + "_PB" : $"{baseName}_PB{i}";
            if (candidate.Length > 63) candidate = candidate[^63..];
            if (!target.Ports.Any(p => string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase))) return candidate;
        }
        return baseName + "_" + Guid.NewGuid().ToString("N")[..6];
    }
}
