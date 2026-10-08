using DataBridge.Printers.Archive;
using DataBridge.Printers.Diagnostics;
using DataBridge.Printers.Inventory;
using DataBridge.Printers.Machine;
using DataBridge.Printers.Model;
using DataBridge.Printers.Reporting;
using DataBridge.Printers.UserScope;
using DataBridge.Printers.Util;

namespace DataBridge.Printers.Restore;

public sealed record RestoreProgress(int Index, int Count, string Printer, string Step);

/// <summary>
/// Executes a <see cref="RestorePlan"/>: machine operations through <see cref="IMachineOps"/> (elevated),
/// user operations in this process. Only the selected printers and the ports/drivers they need are touched.
/// Each printer is independent: a failure is recorded and the next printer is processed.
/// </summary>
public sealed class RestoreEngine
{
    private readonly RestorePlan _plan;
    private readonly Func<IMachineOps> _machineFactory;
    private IMachineOps? _machine;
    private readonly Dictionary<string, (bool ok, string message)> _driverDone = new(StringComparer.OrdinalIgnoreCase);

    public OperationLog Log { get; } = new();
    public Journal Journal { get; } = new();
    public IMachineOps? Machine => _machine;

    public RestoreEngine(RestorePlan plan, Func<IMachineOps> machineFactory)
    {
        _plan = plan;
        _machineFactory = machineFactory;
    }

    private IMachineOps MachineOps => _machine ??= _machineFactory();

    public RestoreReport Run(IProgress<RestoreProgress>? progress = null, CancellationToken ct = default)
    {
        var report = new RestoreReport
        {
            Started = DateTime.Now,
            ArchivePath = _plan.Archive.SourcePath,
            Source = _plan.Archive.Manifest?.Source,
            Target = _plan.Target,
            JournalPath = Journal.Path,
        };
        var selected = _plan.ToRestore.ToList();
        Log.Info($"Restauration de {selected.Count} imprimante(s) depuis « {_plan.Archive.SourcePath} ».");

        // UAC once, up front, only when needed.
        if (_plan.NeedsAdmin)
        {
            progress?.Report(new RestoreProgress(0, selected.Count, "", "Demande des droits administrateur"));
            try
            {
                var m = MachineOps;
                if (m is Elevation.ElevatedMachineOps elevated)
                {
                    report.ElevatedAccount = elevated.WorkerAccount;
                    report.General.Add(new StepResult(StepStatus.Info, "Droits administrateur obtenus", $"Opérations machine exécutées sous « {elevated.WorkerAccount} ». Les opérations du profil utilisateur restent sous « {_plan.ProcessUser} »."));
                }
            }
            catch (Exception ex)
            {
                report.General.Add(new StepResult(StepStatus.Error, "Droits administrateur non obtenus", ex.Message));
                Log.Error("Élévation : " + ex.Message);
            }
        }

        if (!_plan.SpoolerRunning)
        {
            var r = _machine?.StartSpooler() ?? OpResult.Fail("droits administrateur non disponibles");
            report.General.Add(new StepResult(r.Ok ? StepStatus.Ok : StepStatus.Error, "Démarrage du spouleur", r.Message));
            if (!r.Ok)
            {
                report.Finished = DateTime.Now;
                Log.Error("Spouleur arrêté : restauration impossible.");
                report.LogPath = SaveLog();
                return report;
            }
        }

        var appliedStates = new Dictionary<PrinterPlan, AppliedState>();
        int index = 0;
        foreach (var p in selected)
        {
            ct.ThrowIfCancellationRequested();
            index++;
            progress?.Report(new RestoreProgress(index, selected.Count, p.Entry.Name, "Restauration"));
            var pr = new PrinterReport
            {
                Id = p.Entry.Id,
                SourceName = p.Entry.Name,
                TargetName = p.TargetName,
                Kind = Labels.Kind(p.Entry.Kind),
                IsConnection = p.IsConnection,
                PortHost = p.Port?.HostAddress,
                PortNumber = p.Port?.PortNumber ?? 0,
            };
            report.Printers.Add(pr);
            try
            {
                var applied = p.IsConnection ? RestoreConnection(p, pr) : RestoreLocal(p, pr, progress, index, selected.Count);
                if (applied != null) appliedStates[p] = applied;
            }
            catch (Exception ex)
            {
                pr.Steps.Add(new StepResult(StepStatus.Error, "Erreur inattendue", ex.Message));
                pr.Outcome = PrinterOutcome.Failed;
                Log.Error($"« {p.Entry.Name} » : {ex.Message}");
            }
        }

        // Default printer (user profile).
        var defaultPlan = selected.FirstOrDefault(p => p.MakeDefault);
        if (defaultPlan != null && _plan.RunsAsSessionUser)
        {
            var target = report.Printers.First(r => r.Id == defaultPlan.Entry.Id);
            if (target.Outcome is PrinterOutcome.Failed or PrinterOutcome.Skipped)
                report.General.Add(new StepResult(StepStatus.Skipped, "Imprimante par défaut", $"« {defaultPlan.Entry.Name} » n'a pas été restaurée."));
            else
            {
                if (_plan.RestoreLegacyDefaultMode)
                {
                    try
                    {
                        int? before = SystemInfo.LegacyDefaultPrinterMode();
                        SystemInfo.SetLegacyDefaultPrinterMode(1);
                        Journal.Add("Utilisateur", JournalAction.Modified, JournalObject.DefaultPrinter, "LegacyDefaultPrinterMode", "1", new { LegacyDefaultPrinterMode = before });
                        report.General.Add(new StepResult(StepStatus.Ok, "« Laisser Windows gérer mon imprimante par défaut » désactivé", "Comme sur le PC source."));
                    }
                    catch (Exception ex) { report.General.Add(new StepResult(StepStatus.Warning, "Mode de gestion de l'imprimante par défaut", ex.Message)); }
                }
                string? previous = SystemInfo.DefaultPrinter();
                var r = UserOps.SetDefault(target.TargetName);
                Journal.Add("Utilisateur", r.Ok ? JournalAction.Modified : JournalAction.Failed, JournalObject.DefaultPrinter, target.TargetName, r.Message, new { Previous = previous });
                report.General.Add(new StepResult(r.Ok ? StepStatus.Ok : StepStatus.Warning, "Imprimante par défaut", r.Message));
            }
        }

        // Verification: everything is read back from the spooler.
        progress?.Report(new RestoreProgress(selected.Count, selected.Count, "", "Vérification"));
        var context = new LiveInventory
        {
            Environment = SystemInfo.NativeEnvironment(),
            Ports = PortReader.ReadAll(),
            Drivers = DriverReader.ReadAll(SystemInfo.NativeEnvironment(), includeSigner: false),
            DefaultPrinter = SystemInfo.DefaultPrinter(),
        };
        foreach (var (p, applied) in appliedStates)
        {
            var pr = report.Printers.First(r => r.Id == p.Entry.Id);
            pr.Checks = Verifier.Verify(p, applied, _plan.Archive, context);
            if (pr.Outcome == PrinterOutcome.Restored && pr.Checks.Any(c => c.Status is CheckStatus.Different or CheckStatus.Missing))
                pr.Outcome = PrinterOutcome.RestoredWithDifferences;
            if (pr.Checks.Any(c => c.Category == "Configuration" && c.Label == "Imprimante" && c.Status == CheckStatus.Missing))
                pr.Outcome = PrinterOutcome.Failed;
        }
        if (defaultPlan != null)
        {
            var target = report.Printers.First(r => r.Id == defaultPlan.Entry.Id);
            bool isDefault = string.Equals(context.DefaultPrinter, target.TargetName, StringComparison.OrdinalIgnoreCase);
            target.Checks.Add(new CheckItem("Utilisateur", "Imprimante par défaut", target.TargetName, context.DefaultPrinter, isDefault ? CheckStatus.Identical : CheckStatus.Different));
        }

        report.Finished = DateTime.Now;
        report.LogPath = SaveLog();
        return report;
    }

    private string SaveLog()
    {
        string path = Path.Combine(OperationLog.LogsFolder, Path.GetFileName(Journal.Path).Replace(".journal.jsonl", ".log"));
        try { File.WriteAllText(path, Log.Text); } catch { }
        return path;
    }

    private AppliedState? RestoreLocal(PrinterPlan p, PrinterReport pr, IProgress<RestoreProgress>? progress, int index, int count)
    {
        var e = p.Entry;
        var applied = new AppliedState { TargetName = p.TargetName };
        if (p.NameAction == NameAction.KeepExisting)
        {
            pr.Steps.Add(new StepResult(StepStatus.Info, "Imprimante existante conservée", "Aucune modification de la file existante."));
            Journal.Add("Machine", JournalAction.Unchanged, JournalObject.Printer, e.Name, "Conservée à la demande de l'utilisateur.");
            pr.Outcome = PrinterOutcome.KeptExisting;
            return null;
        }
        if (_machine == null)
        {
            pr.Steps.Add(new StepResult(StepStatus.Error, "Droits administrateur", "Indispensables pour créer une file locale."));
            pr.Outcome = PrinterOutcome.Failed;
            return null;
        }

        // 1. Driver.
        progress?.Report(new RestoreProgress(index, count, e.Name, "Pilote"));
        if (!EnsureDriver(p, pr))
        {
            pr.Outcome = PrinterOutcome.Failed;
            return null;
        }

        // 2. Port.
        progress?.Report(new RestoreProgress(index, count, e.Name, "Port"));
        string? portName = p.TargetPortName;
        if (portName == null)
        {
            pr.Steps.Add(new StepResult(StepStatus.Error, "Port", "Nom de port manquant."));
            pr.Outcome = PrinterOutcome.Failed;
            return null;
        }
        applied.PortName = portName;
        switch (p.PortAction)
        {
            case PortAction.Create:
            case PortAction.CreateWithOtherName:
            {
                var r = _machine.AddTcpPort(p.Port!, portName);
                Journal.Add("Machine", r.Ok ? JournalAction.Created : JournalAction.Failed, JournalObject.Port, portName, r.Message);
                pr.Steps.Add(new StepResult(r.Ok ? StepStatus.Ok : StepStatus.Error, "Création du port", r.Message));
                if (!r.Ok) { pr.Outcome = PrinterOutcome.Failed; return null; }
                break;
            }
            case PortAction.ReuseIdentical:
                pr.Steps.Add(new StepResult(StepStatus.Ok, "Port existant réutilisé", $"« {portName} » a déjà la même configuration."));
                Journal.Add("Machine", JournalAction.Unchanged, JournalObject.Port, portName, "Identique, réutilisé.");
                break;
            case PortAction.KeepExisting:
                pr.Steps.Add(new StepResult(StepStatus.Warning, "Port existant conservé", p.ExistingPortInfo));
                Journal.Add("Machine", JournalAction.Unchanged, JournalObject.Port, portName, "Conservé malgré une configuration différente.");
                break;
            case PortAction.Reconfigure:
            {
                var before = PortReader.ReadOne(portName);
                var r = _machine.ConfigureTcpPort(p.Port!, portName);
                Journal.Add("Machine", r.Ok ? JournalAction.Modified : JournalAction.Failed, JournalObject.Port, portName, r.Message, before);
                pr.Steps.Add(new StepResult(r.Ok ? StepStatus.Ok : StepStatus.Error, "Reconfiguration du port", r.Message));
                if (!r.Ok) { pr.Outcome = PrinterOutcome.Failed; return null; }
                break;
            }
        }

        // 3. Queue.
        progress?.Report(new RestoreProgress(index, count, e.Name, "File d'impression"));
        if (p.NameAction == NameAction.Replace)
        {
            LivePrinter? before = null;
            try { before = PrinterInventory.ReadPrinter(e.Name, 0, details: false, new LiveInventory()); } catch { }
            var d = _machine.DeletePrinter(e.Name);
            Journal.Add("Machine", d.Ok ? JournalAction.Deleted : JournalAction.Failed, JournalObject.Printer, e.Name, d.Message, before?.Entry);
            pr.Steps.Add(new StepResult(d.Ok ? StepStatus.Ok : StepStatus.Error, "Suppression de l'imprimante existante (remplacement demandé)", d.Message));
            if (!d.Ok) { pr.Outcome = PrinterOutcome.Failed; return null; }
        }
        applied.ShareRequested = p.RestoreShare && e.IsShared && !string.IsNullOrEmpty(e.ShareName);
        var create = new PrinterCreateRequest
        {
            Name = p.TargetName,
            PortName = portName,
            DriverName = e.DriverName!,
            PrintProcessor = e.PrintProcessor,
            Datatype = e.Datatype,
            Comment = e.Comment,
            Location = e.Location,
            ShareName = e.ShareName,
            Shared = applied.ShareRequested,
            Attributes = e.Attributes,
            Priority = e.Priority,
            DefaultPriority = e.DefaultPriority,
            StartTime = e.StartTime,
            UntilTime = e.UntilTime,
            SepFile = e.SepFile,
            Parameters = e.Parameters,
        };
        var add = _machine.AddPrinter(create);
        if (!add.Ok && applied.ShareRequested)
        {
            // Sharing failures (name taken, firewall/service policy) must not prevent the queue itself.
            pr.Steps.Add(new StepResult(StepStatus.Warning, "Partage", $"Création avec partage refusée ({add.Message}) : nouvel essai sans partage."));
            create.Shared = false;
            applied.ShareRequested = false;
            add = _machine.AddPrinter(create);
        }
        Journal.Add("Machine", add.Ok ? JournalAction.Created : JournalAction.Failed, JournalObject.Printer, p.TargetName, add.Message);
        pr.Steps.Add(new StepResult(add.Ok ? StepStatus.Ok : StepStatus.Error, "Création de la file", add.Message));
        if (!add.Ok) { pr.Outcome = PrinterOutcome.Failed; return null; }
        if (applied.ShareRequested) pr.Steps.Add(new StepResult(StepStatus.Ok, "Partage", $"Partagée sous « {e.ShareName} »."));

        bool partial = false;
        bool sameDriver = p.Driver != null && string.Equals(DriverReader.Find(e.DriverName!, SystemInfo.NativeEnvironment())?.Version, p.Driver.Version, StringComparison.Ordinal);

        // 4. Installed options / driver data (before the DEVMODE: its validation depends on the installed options).
        progress?.Report(new RestoreProgress(index, count, e.Name, "Options du pilote"));
        if (p.RestoreDriverData && e.DriverData != null)
        {
            var file = _plan.Archive.VerifiedPath(e.DriverData.File);
            if (!sameDriver)
                pr.Steps.Add(new StepResult(StepStatus.Skipped, "Options et données du pilote", "Pilote différent de la source : ces données brutes ne sont restaurées qu'avec la même version du pilote."));
            else if (file == null)
            { pr.Steps.Add(new StepResult(StepStatus.Warning, "Options et données du pilote", "Fichier manquant ou endommagé dans l'archive.")); partial = true; }
            else
            {
                var values = Json.Deserialize<List<PrinterDataValue>>(File.ReadAllText(file)) ?? new();
                var r = _machine.SetDriverData(p.TargetName, values);
                Journal.Add("Machine", r.Ok ? JournalAction.Modified : JournalAction.Failed, JournalObject.Settings, p.TargetName, "Données du pilote : " + r.Message);
                pr.Steps.Add(new StepResult(r.Ok && !r.Message!.Contains("refusée") ? StepStatus.Ok : StepStatus.Warning, "Options et données du pilote", r.Message));
                applied.DriverDataWritten = r.Ok;
                if (!r.Ok || r.Message!.Contains("refusée")) partial = true;
            }
        }

        // 5. Global defaults.
        progress?.Report(new RestoreProgress(index, count, e.Name, "Réglages par défaut"));
        if (p.RestoreGlobalSettings && e.GlobalSettings != null)
        {
            try
            {
                var computed = SettingsApplier.Compute(p.TargetName, e.GlobalSettings, _plan.Archive, sameDriver, userLayer: false);
                var r = _machine.SetGlobalDevMode(p.TargetName, computed.DevMode);
                Journal.Add("Machine", r.Ok ? JournalAction.Modified : JournalAction.Failed, JournalObject.Settings, p.TargetName, "Réglages par défaut : " + r.Message);
                string detail = $"{r.Message} Méthode : {SettingsApplier.Describe(computed.Tier)}." + (computed.Skipped.Count > 0 ? " Non appliqués : " + string.Join(" ; ", computed.Skipped) : "");
                pr.Steps.Add(new StepResult(r.Ok ? (computed.Skipped.Count > 0 ? StepStatus.Warning : StepStatus.Ok) : StepStatus.Error, "Réglages par défaut de la file", detail));
                applied.GlobalSettingsWritten = r.Ok;
                applied.GlobalTier = computed.Tier;
                if (!r.Ok || computed.Skipped.Count > 0 || computed.Tier != SettingsTier.FullDevMode) partial |= !r.Ok;
            }
            catch (Exception ex)
            {
                pr.Steps.Add(new StepResult(StepStatus.Error, "Réglages par défaut de la file", ex.Message));
                partial = true;
            }
        }

        // 6. Permissions.
        if (p.RestoreSecurity && e.Security != null)
        {
            var sec = p.Security ?? SecurityPlanner.Build(e.Security, p.MapLocalAccountsByName);
            if (sec.Sddl == null)
            {
                pr.Steps.Add(new StepResult(StepStatus.Warning, "Permissions", string.Join(" ", sec.Notes)));
                partial = true;
            }
            else
            {
                var r = _machine.SetSecurity(p.TargetName, sec.Sddl);
                Journal.Add("Machine", r.Ok ? JournalAction.Modified : JournalAction.Failed, JournalObject.Security, p.TargetName, r.Message);
                pr.Steps.Add(new StepResult(r.Ok ? (sec.Notes.Count > 0 ? StepStatus.Warning : StepStatus.Ok) : StepStatus.Error, "Permissions",
                    r.Message + (sec.Notes.Count > 0 ? " " + string.Join(" ", sec.Notes) : "")));
                if (r.Ok) applied.SecuritySddl = sec.Sddl; else partial = true;
            }
        }

        // 7. Preferences of the current user.
        progress?.Report(new RestoreProgress(index, count, e.Name, "Préférences utilisateur"));
        RestoreUserSettings(p, pr, applied, sameDriver, ref partial);

        pr.Outcome = partial ? PrinterOutcome.Partial : PrinterOutcome.Restored;
        return applied;
    }

    private AppliedState? RestoreConnection(PrinterPlan p, PrinterReport pr)
    {
        var e = p.Entry;
        var applied = new AppliedState { TargetName = e.Name };
        if (p.DriverAction is DriverAction.InstallFromArchive or DriverAction.InstallUserPackage)
        {
            if (_machine == null)
                pr.Steps.Add(new StepResult(StepStatus.Warning, "Pilote", "Droits administrateur non disponibles : le pilote n'a pas été préinstallé."));
            else if (!EnsureDriver(p, pr))
                pr.Steps.Add(new StepResult(StepStatus.Warning, "Pilote", "Préinstallation impossible : la connexion va tenter d'obtenir le pilote du serveur."));
        }

        if (p.NameAction == NameAction.KeepExisting)
        {
            pr.Steps.Add(new StepResult(StepStatus.Info, "Connexion existante conservée", e.Name));
            Journal.Add("Utilisateur", JournalAction.Unchanged, JournalObject.Connection, e.Name, "Déjà présente.");
        }
        else
        {
            if (p.NameAction == NameAction.Replace)
            {
                var d = UserOps.DeleteConnection(e.Name);
                Journal.Add("Utilisateur", d.Ok ? JournalAction.Deleted : JournalAction.Failed, JournalObject.Connection, e.Name, d.Message);
                pr.Steps.Add(new StepResult(d.Ok ? StepStatus.Ok : StepStatus.Error, "Suppression de la connexion existante", d.Message));
                if (!d.Ok) { pr.Outcome = PrinterOutcome.Failed; return null; }
            }
            var r = UserOps.AddConnection(e.Name);
            Journal.Add("Utilisateur", r.Ok ? JournalAction.Created : JournalAction.Failed, JournalObject.Connection, e.Name, r.Message);
            pr.Steps.Add(new StepResult(r.Ok ? StepStatus.Ok : StepStatus.Error, "Connexion au partage", r.Message));
            if (!r.Ok) { pr.Outcome = PrinterOutcome.Failed; return null; }
        }

        bool partial = false;
        string? installedVersion = e.DriverName == null ? null : DriverReader.Find(e.DriverName, SystemInfo.NativeEnvironment())?.Version;
        bool sameDriver = p.Driver != null && installedVersion == p.Driver.Version;
        RestoreUserSettings(p, pr, applied, sameDriver, ref partial);
        pr.Outcome = p.NameAction == NameAction.KeepExisting && !applied.UserSettingsWritten ? PrinterOutcome.KeptExisting
            : partial ? PrinterOutcome.Partial : PrinterOutcome.Restored;
        return applied;
    }

    private void RestoreUserSettings(PrinterPlan p, PrinterReport pr, AppliedState applied, bool sameDriver, ref bool partial)
    {
        var layer = p.Entry.UserSettings;
        if (!p.RestoreUserSettings || layer == null) return;
        if (!_plan.RunsAsSessionUser)
        {
            pr.Steps.Add(new StepResult(StepStatus.Skipped, "Préférences de l'utilisateur", "DataBridge ne s'exécute pas sous le compte de la session."));
            return;
        }
        if (!layer.DiffersFromGlobal)
        {
            pr.Steps.Add(new StepResult(StepStatus.Info, "Préférences de l'utilisateur", "Identiques aux valeurs de la file sur la source : rien à restaurer."));
            return;
        }
        try
        {
            var computed = SettingsApplier.Compute(applied.TargetName, layer, _plan.Archive, sameDriver, userLayer: true);
            var r = UserOps.SetUserDevMode(applied.TargetName, computed.DevMode);
            Journal.Add("Utilisateur", r.Ok ? JournalAction.Modified : JournalAction.Failed, JournalObject.Settings, applied.TargetName, "Préférences : " + r.Message);
            string detail = $"{r.Message} Méthode : {SettingsApplier.Describe(computed.Tier)}." + (computed.Skipped.Count > 0 ? " Non appliqués : " + string.Join(" ; ", computed.Skipped) : "");
            pr.Steps.Add(new StepResult(r.Ok ? (computed.Skipped.Count > 0 ? StepStatus.Warning : StepStatus.Ok) : StepStatus.Error, "Préférences de l'utilisateur", detail));
            applied.UserSettingsWritten = r.Ok;
            applied.UserTier = computed.Tier;
            if (!r.Ok) partial = true;
        }
        catch (Exception ex)
        {
            pr.Steps.Add(new StepResult(StepStatus.Error, "Préférences de l'utilisateur", ex.Message));
            partial = true;
        }
    }

    /// <summary>Installs (or confirms) the driver for this printer. Each driver is processed once per restoration.</summary>
    private bool EnsureDriver(PrinterPlan p, PrinterReport pr)
    {
        string name = p.Entry.DriverName!;
        string env = SystemInfo.NativeEnvironment();
        string key = name + "|" + p.DriverAction + "|" + p.UserInfPath;
        if (_driverDone.TryGetValue(key, out var done))
        {
            pr.Steps.Add(new StepResult(done.ok ? StepStatus.Ok : StepStatus.Error, "Pilote", done.message + " (déjà traité pour une autre imprimante)"));
            return done.ok;
        }

        OpResult result;
        switch (p.DriverAction)
        {
            case DriverAction.UseInstalled:
            {
                var installed = DriverReader.Find(name, env);
                result = installed != null
                    ? OpResult.Success($"Pilote « {name} » déjà installé (version {installed.Version}).")
                    : OpResult.Fail($"Le pilote « {name} » n'est plus installé.");
                if (result.Ok) Journal.Add("Machine", JournalAction.Unchanged, JournalObject.Driver, name, installed!.Version);
                break;
            }
            case DriverAction.InstallInbox:
                result = _machine!.InstallDriver(name, env, null);
                Journal.Add("Machine", result.Ok ? JournalAction.Created : JournalAction.Failed, JournalObject.Driver, name, result.Message);
                break;
            case DriverAction.InstallFromArchive:
            {
                var d = p.Driver!;
                string packageDir = Path.Combine(_plan.Archive.Root, d.PackageFolder!.Replace('/', '\\'));
                string prefix = d.PackageFolder!.TrimEnd('/') + "/";
                var request = new DriverPackageRequest
                {
                    PackageDir = packageDir,
                    InfRelative = d.PackageInf![prefix.Length..],
                    Environment = env,
                    Hashes = _plan.Archive.Manifest!.Files.Where(f => f.Key.StartsWith(prefix, StringComparison.Ordinal))
                        .ToDictionary(f => f.Key[prefix.Length..], f => f.Value),
                };
                result = InstallPackage(name, env, request, $"archive, version {d.Version}");
                break;
            }
            case DriverAction.InstallUserPackage:
            {
                string inf = p.UserInfPath!;
                string dir = Path.GetDirectoryName(inf)!;
                if (!DriverReader.InfMentionsModel(inf, name))
                {
                    result = OpResult.Fail($"Le package choisi ({Path.GetFileName(inf)}) ne déclare pas le modèle « {name} ». Aucun pilote de remplacement n'est utilisé sans votre choix explicite.");
                    break;
                }
                if (!DriverReader.SupportsEnvironment(DriverReader.ReadInfArchitectures(inf), env))
                {
                    result = OpResult.Fail($"Le package choisi ne contient pas de version {env}.");
                    break;
                }
                var request = new DriverPackageRequest
                {
                    PackageDir = dir,
                    InfRelative = Path.GetFileName(inf),
                    Environment = env,
                    Hashes = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                        .ToDictionary(f => Path.GetRelativePath(dir, f).Replace('\\', '/'), Hashing.Sha256File),
                };
                result = InstallPackage(name, env, request, $"package choisi : {inf}");
                break;
            }
            case DriverAction.FromServer:
                result = OpResult.Success("Le pilote sera fourni par le serveur d'impression.");
                break;
            default:
                result = OpResult.Fail("Aucun pilote disponible.");
                break;
        }
        _driverDone[key] = (result.Ok, result.Message ?? "");
        pr.Steps.Add(new StepResult(result.Ok ? StepStatus.Ok : StepStatus.Error, "Pilote", result.Message));
        Log.Info($"Pilote « {name} » : {result.Message}");
        return result.Ok;
    }

    private OpResult InstallPackage(string name, string env, DriverPackageRequest request, string origin)
    {
        var staged = _machine!.StageDriverPackage(request);
        Journal.Add("Machine", staged.Ok ? JournalAction.Created : JournalAction.Failed, JournalObject.DriverPackage, name, $"{origin} — {staged.Message} {staged.Value}");
        if (!staged.Ok) return staged;
        var installed = _machine.InstallDriver(name, env, staged.Value);
        Journal.Add("Machine", installed.Ok ? JournalAction.Created : JournalAction.Failed, JournalObject.Driver, name, installed.Message);
        if (!installed.Ok) return installed;
        return OpResult.Success($"{staged.Message} {installed.Message}", installed.Value);
    }

    /// <summary>Connectivity probes, kept separate from the configuration result.</summary>
    public static async Task ProbeAsync(PrinterReport pr)
    {
        if (pr.IsConnection)
        {
            Inventory.PrinterInventory.SplitUnc(pr.TargetName, out var server, out _);
            string host = server?.TrimStart('\\') ?? "";
            var tcp = await Reachability.TcpAsync(host, 445, TimeSpan.FromSeconds(4));
            pr.Reachable = tcp.Success;
            pr.Reachability = "Serveur : " + tcp.Detail;
            var share = await Task.Run(() => Reachability.ShareAccess(pr.TargetName));
            pr.ShareOk = share.Success;
            pr.ShareAccess = share.Detail;
        }
        else if (!string.IsNullOrEmpty(pr.PortHost))
        {
            var tcp = await Reachability.TcpAsync(pr.PortHost, pr.PortNumber > 0 ? pr.PortNumber : 9100, TimeSpan.FromSeconds(4));
            pr.Reachable = tcp.Success;
            pr.Reachability = "Imprimante : " + tcp.Detail;
        }
    }
}
