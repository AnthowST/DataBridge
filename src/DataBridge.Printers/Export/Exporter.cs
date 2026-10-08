using System.IO.Compression;
using System.Reflection;
using DataBridge.Printers.Archive;
using DataBridge.Printers.Inventory;
using DataBridge.Printers.Model;
using DataBridge.Printers.Native;
using DataBridge.Printers.Util;

namespace DataBridge.Printers.Export;

public sealed class ExportOptions
{
    /// <summary>
    /// Include the driver-private part of DEVMODEs and the PrinterDriverData values. They carry the vendor options
    /// but may also contain accounting codes or user names that cannot be filtered out of an opaque block.
    /// </summary>
    public bool IncludePrivateDriverData { get; set; } = true;
}

public sealed record ExportProgress(int Index, int Count, string Printer, string Step);

public sealed class ExportResult
{
    public string ArchivePath { get; init; } = "";
    public Manifest Manifest { get; init; } = new();
    public InventoryDocument Inventory { get; init; } = new();
    public OperationLog Log { get; init; } = new();
    public long ArchiveSize { get; init; }
    public List<ArchiveCheck> Verification { get; init; } = new();
}

public static class Exporter
{
    public const string Extension = ".prnbak";

    public static string ToolVersion =>
        typeof(Exporter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";

    public static ExportResult Export(LiveInventory inv, IReadOnlyCollection<string> selectedIds, string archivePath, ExportOptions options,
        IProgress<ExportProgress>? progress = null, OperationLog? log = null, CancellationToken ct = default)
    {
        log ??= new OperationLog();
        var selected = inv.Printers.Where(p => selectedIds.Contains(p.Entry.Id)).ToList();
        if (selected.Count == 0) throw new InvalidOperationException("Aucune imprimante sélectionnée.");

        string staging = Path.Combine(Path.GetTempPath(), "DataBridge", "printers-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        log.Info($"Export de {selected.Count} imprimante(s) vers « {archivePath} ».");
        try
        {
            var manifest = new Manifest
            {
                ToolVersion = ToolVersion,
                ExportedAt = DateTimeOffset.Now,
                Source = SystemInfo.Collect(),
                PrivateDriverDataIncluded = options.IncludePrivateDriverData,
            };
            manifest.Notes.Add("Les empreintes SHA-256 permettent de vérifier que l'archive n'a pas été modifiée ou endommagée. Elles ne prouvent pas que la source est digne de confiance.");
            if (options.IncludePrivateDriverData)
                manifest.Notes.Add("L'archive contient les données privées des pilotes (options constructeur). Elles peuvent inclure des codes de comptabilité ou des noms d'utilisateur.");
            var doc = new InventoryDocument { LegacyDefaultPrinterMode = inv.LegacyDefaultPrinterMode };

            // Ports and drivers referenced by the selection, each exported once.
            var portIds = new Dictionary<string, PortEntry>(StringComparer.OrdinalIgnoreCase);
            var driverIds = new Dictionary<string, DriverEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var lp in selected)
            {
                var e = lp.Entry;
                if (e.Kind != PrinterKind.SharedConnection && e.Kind != PrinterKind.MachineConnection && e.PortName != null && !portIds.ContainsKey(e.PortName))
                {
                    var port = inv.Ports.FirstOrDefault(p => string.Equals(p.Name, e.PortName, StringComparison.OrdinalIgnoreCase));
                    if (port != null)
                    {
                        var copy = Clone(port);
                        copy.Id = $"T{portIds.Count + 1:000}";
                        portIds[e.PortName] = copy;
                    }
                }
                if (e.DriverName != null && !driverIds.ContainsKey(e.DriverName))
                {
                    var drv = inv.Drivers.FirstOrDefault(d => string.Equals(d.Name, e.DriverName, StringComparison.OrdinalIgnoreCase));
                    if (drv != null)
                    {
                        var copy = Clone(drv);
                        copy.Id = $"D{driverIds.Count + 1:000}";
                        driverIds[e.DriverName] = copy;
                    }
                }
                if (e.DriverName != null && driverIds.TryGetValue(e.DriverName, out var used)) used.UsedByCount++;
            }

            int index = 0;
            foreach (var lp in selected)
            {
                ct.ThrowIfCancellationRequested();
                index++;
                progress?.Report(new ExportProgress(index, selected.Count + driverIds.Count, lp.Entry.Name, "Configuration et réglages"));
                var entry = ExportPrinter(lp, staging, options, portIds, driverIds, log);
                doc.Printers.Add(entry);
            }

            foreach (var drv in driverIds.Values)
            {
                ct.ThrowIfCancellationRequested();
                index++;
                progress?.Report(new ExportProgress(index, selected.Count + driverIds.Count, drv.Name, "Package du pilote"));
                ExportDriver(drv, staging, log);
            }

            // Driver outcome is reported on every printer using it.
            foreach (var p in doc.Printers)
            {
                if (p.DriverId == null)
                {
                    p.Components.Add(new ComponentStatus(p.Id, "Pilote", ComponentState.NotApplicable,
                        p.Kind == PrinterKind.SharedConnection ? "Pilote inconnu (serveur injoignable) : il sera fourni par le serveur." : "Pilote introuvable."));
                    continue;
                }
                var d = driverIds.Values.First(x => x.Id == p.DriverId);
                if (d.IsInbox)
                    p.Components.Add(new ComponentStatus(p.Id, "Pilote", ComponentState.NotApplicable, $"Pilote intégré à Windows ({d.InfOriginalName}) : non exporté, il doit être présent sur le PC cible."));
                else if (d.Exported)
                    p.Components.Add(new ComponentStatus(p.Id, "Pilote", ComponentState.Exported, $"Package {d.InfOriginalName} ({d.PublishedInf}) version {d.Version}, signé par {d.Signer ?? "éditeur non déterminé"}."));
                else
                    p.Components.Add(new ComponentStatus(p.Id, "Pilote", ComponentState.NotExported, d.NotExportedReason));
            }

            doc.Ports = portIds.Values.OrderBy(p => p.Id).ToList();
            doc.Drivers = driverIds.Values.OrderBy(d => d.Id).ToList();
            var defaultPrinter = selected.FirstOrDefault(p => p.Entry.IsDefault);
            doc.DefaultPrinter = defaultPrinter?.Entry.Name;

            WriteText(staging, "inventory.json", Json.Serialize(doc));
            foreach (var p in doc.Printers) manifest.Components.AddRange(p.Components);
            WriteText(staging, "report/export-report.txt", ExportReportText(manifest, doc));
            WriteText(staging, "logs/export.log", log.Text);

            foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(staging, file).Replace('\\', '/');
                manifest.Files[rel] = Hashing.Sha256File(file);
            }
            WriteText(staging, "manifest.json", Json.Serialize(manifest));

            progress?.Report(new ExportProgress(index, index, "", "Création de l'archive"));
            string? dir = Path.GetDirectoryName(Path.GetFullPath(archivePath));
            if (dir != null) Directory.CreateDirectory(dir);
            string temp = archivePath + ".tmp";
            if (File.Exists(temp)) File.Delete(temp);
            ZipFile.CreateFromDirectory(staging, temp, CompressionLevel.Optimal, includeBaseDirectory: false);
            File.Move(temp, archivePath, overwrite: true);

            // Read-back: the archive is reopened and every hash checked, as an import would.
            progress?.Report(new ExportProgress(index, index, "", "Vérification de l'archive"));
            List<ArchiveCheck> verification;
            using (var opened = ArchiveReader.Open(archivePath))
            {
                verification = opened.Checks;
                if (!opened.IsUsable) throw new InvalidOperationException("L'archive produite ne passe pas la vérification : " +
                    string.Join(" ; ", opened.Checks.Where(c => c.Level == CheckLevel.Error).Select(c => c.Detail)));
            }
            log.Info($"Archive créée et vérifiée ({new FileInfo(archivePath).Length / 1024} Ko).");
            return new ExportResult
            {
                ArchivePath = archivePath,
                Manifest = manifest,
                Inventory = doc,
                Log = log,
                ArchiveSize = new FileInfo(archivePath).Length,
                Verification = verification,
            };
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { }
        }
    }

    private static PrinterEntry ExportPrinter(LivePrinter lp, string staging, ExportOptions options,
        Dictionary<string, PortEntry> ports, Dictionary<string, DriverEntry> drivers, OperationLog log)
    {
        var e = Clone(lp.Entry);
        e.Components.Clear();
        e.Warnings.Clear();
        string dir = $"printers/{e.Id}";
        log.Info($"{e.Id} « {e.Name} » ({Labels.Kind(e.Kind)}).");

        if (e.Support == SupportLevel.DetectedOnly && e.SupportNote != null) e.Warnings.Add(e.SupportNote);
        if (e.IsOffline) e.Warnings.Add("Imprimante hors ligne lors de l'export : seules les informations locales ont été enregistrées.");

        var queueIssue = lp.ReadIssues.FirstOrDefault(i => i.Component == "Configuration de la file");
        e.Components.Add(queueIssue != null
            ? new ComponentStatus(e.Id, "Configuration de la file", queueIssue.State, queueIssue.Detail)
            : new ComponentStatus(e.Id, "Configuration de la file", ComponentState.Exported, "Nom, commentaire, emplacement, attributs, processeur d'impression."));

        if (e.PortName != null && ports.TryGetValue(e.PortName, out var port))
        {
            e.PortId = port.Id;
            e.Components.Add(new ComponentStatus(e.Id, "Port", port.Kind == PortKind.StandardTcpIp ? ComponentState.Exported : ComponentState.Partial,
                port.Kind == PortKind.StandardTcpIp ? $"{port.Name} : {port.Describe()}" : $"{port.Name} : type {port.Kind}, non recréable par cette version."));
            if (port.SnmpEnabled && !string.IsNullOrEmpty(port.SnmpCommunity) && port.SnmpCommunity != "public")
                e.Warnings.Add("La communauté SNMP du port est enregistrée dans l'archive (ce n'est pas un mot de passe d'utilisateur, mais elle donne accès en lecture à l'imprimante).");
        }
        else if (e.Kind == PrinterKind.SharedConnection)
        {
            e.Components.Add(new ComponentStatus(e.Id, "Connexion", ComponentState.Exported, $"Chemin UNC {e.Name}"));
        }

        if (e.DriverName != null && drivers.TryGetValue(e.DriverName, out var drv)) e.DriverId = drv.Id;

        // Settings layers.
        string? BinName(short id) => lp.Bins.FirstOrDefault(b => b.id == id).name;
        if (lp.GlobalDevMode != null && DevMode.TryParse(lp.GlobalDevMode, out var gdm))
        {
            e.GlobalSettings = WriteLayer(staging, dir, "global", gdm!, lp.GlobalTicket, options, BinName);
            e.Components.Add(new ComponentStatus(e.Id, "Réglages par défaut de la file", ComponentState.Exported,
                $"DEVMODE ({gdm!.Size} + {gdm.DriverExtra} octets privés{(options.IncludePrivateDriverData ? "" : ", partie privée exclue")})" + (lp.GlobalTicket != null ? " et PrintTicket." : ", sans PrintTicket.")));
        }
        else
        {
            var issue = lp.ReadIssues.FirstOrDefault(i => i.Component.StartsWith("Réglages par défaut", StringComparison.Ordinal));
            e.Components.Add(new ComponentStatus(e.Id, "Réglages par défaut de la file", ComponentState.NotExported, issue?.Detail ?? "Non lisibles."));
        }

        if (lp.UserDevMode != null && DevMode.TryParse(lp.UserDevMode, out var udm))
        {
            e.UserSettings = WriteLayer(staging, dir, "user", udm!, lp.UserTicket, options, BinName);
            e.UserSettings.DiffersFromGlobal = lp.GlobalDevMode == null || !lp.UserDevMode.AsSpan().SequenceEqual(lp.GlobalDevMode);
            e.Components.Add(new ComponentStatus(e.Id, "Préférences de l'utilisateur", ComponentState.Exported,
                e.UserSettings.DiffersFromGlobal ? "Préférences personnelles différentes des valeurs de la file." : "Préférences personnelles identiques aux valeurs de la file."));
        }
        else
        {
            var issue = lp.ReadIssues.FirstOrDefault(i => i.Component == "Préférences de l'utilisateur");
            e.Components.Add(issue != null
                ? new ComponentStatus(e.Id, "Préférences de l'utilisateur", ComponentState.Failed, issue.Detail)
                : new ComponentStatus(e.Id, "Préférences de l'utilisateur", ComponentState.NotApplicable, "Aucune préférence personnelle : l'utilisateur utilise les valeurs de la file."));
        }

        if (e.Kind == PrinterKind.SharedConnection)
        {
            e.Components.Add(new ComponentStatus(e.Id, "Options et données du pilote", ComponentState.NotApplicable, "Gérées par le serveur d'impression."));
        }
        else if (!options.IncludePrivateDriverData)
        {
            e.Components.Add(new ComponentStatus(e.Id, "Options et données du pilote", ComponentState.NotExported, "Exclues à la demande (données privées du pilote)."));
        }
        else if (lp.DriverData != null)
        {
            string file = $"{dir}/driverdata.json";
            WriteText(staging, file, Json.Serialize(lp.DriverData));
            e.DriverData = new DriverDataInfo { File = file, ValueCount = lp.DriverData.Count };
            e.Components.Add(new ComponentStatus(e.Id, "Options et données du pilote", ComponentState.Exported,
                $"{lp.DriverData.Count} valeur(s) brutes. Restaurables seulement avec le même pilote dans la même version."));
        }
        else
        {
            var issue = lp.ReadIssues.FirstOrDefault(i => i.Component == "Options et données du pilote");
            e.Components.Add(new ComponentStatus(e.Id, "Options et données du pilote", ComponentState.NotExported, issue?.Detail ?? "Non lisibles."));
        }

        if (e.Kind == PrinterKind.SharedConnection)
        {
            e.Components.Add(new ComponentStatus(e.Id, "Partage et permissions", ComponentState.NotApplicable, "Le partage et ses droits appartiennent au serveur."));
        }
        else
        {
            e.Components.Add(new ComponentStatus(e.Id, "Partage", ComponentState.Exported, e.IsShared ? $"Partagée sous « {e.ShareName} »." : "Non partagée."));
            if (e.Security != null)
            {
                e.Components.Add(new ComponentStatus(e.Id, "Permissions", e.Security.HasNonPortableEntries || e.Security.HasUnsupportedAces ? ComponentState.Partial : ComponentState.Exported,
                    $"{e.Security.Aces.Count} entrée(s)." + (e.Security.HasNonPortableEntries ? " Certaines identités (comptes locaux ou introuvables) ne pourront pas être résolues sur un autre PC." : "")));
                if (e.Security.HasNonPortableEntries)
                {
                    var names = e.Security.Aces.Where(a => a.Portability is SidPortability.LocalAccount or SidPortability.Unresolved)
                        .Select(a => a.Account ?? a.Sid).Distinct();
                    e.Warnings.Add("Identités non transférables telles quelles : " + string.Join(", ", names) + ".");
                }
            }
            else
            {
                var issue = lp.ReadIssues.FirstOrDefault(i => i.Component == "Permissions");
                e.Components.Add(new ComponentStatus(e.Id, "Permissions", ComponentState.NotExported, issue?.Detail ?? "Non lues."));
            }
        }

        foreach (var issue in lp.ReadIssues.Where(i => i.Component.StartsWith("PrintTicket", StringComparison.Ordinal)))
            e.Warnings.Add($"{issue.Component} : {issue.Detail}");
        foreach (var c in e.Components) c.Target = e.Id;
        return e;
    }

    private static SettingsLayer WriteLayer(string staging, string dir, string name, DevMode dm, byte[]? ticket, ExportOptions options, Func<short, string?> binName)
    {
        var layer = new SettingsLayer
        {
            Summary = dm.ToSummary(binName),
            PrivatePartExcluded = !options.IncludePrivateDriverData && dm.DriverExtra > 0,
        };
        byte[] bytes = options.IncludePrivateDriverData ? dm.Bytes : dm.WithoutPrivatePart();
        layer.DevModeFile = $"{dir}/devmode-{name}.bin";
        WriteBytes(staging, layer.DevModeFile, bytes);
        if (ticket != null)
        {
            layer.PrintTicketFile = $"{dir}/ticket-{name}.xml";
            WriteBytes(staging, layer.PrintTicketFile, ticket);
        }
        return layer;
    }

    private static void ExportDriver(DriverEntry d, string staging, OperationLog log)
    {
        if (d.IsInbox)
        {
            log.Info($"Pilote « {d.Name} » : intégré à Windows, non exporté.");
            return;
        }
        if (d.PublishedInf == null)
        {
            d.NotExportedReason = string.IsNullOrEmpty(d.InfPath)
                ? "Pilote installé sans package (ancienne méthode) : aucun package installable ne peut être extrait proprement."
                : "Package introuvable dans le magasin de pilotes.";
            log.Warn($"Pilote « {d.Name} » : {d.NotExportedReason}");
            return;
        }
        string folder = $"drivers/{d.Id}/package";
        string dest = Path.Combine(staging, folder.Replace('/', '\\'));
        Directory.CreateDirectory(dest);
        try
        {
            var r = PnpUtil.Run("/export-driver", d.PublishedInf, dest);
            string? inf = d.InfOriginalName == null ? null
                : Directory.EnumerateFiles(dest, d.InfOriginalName, SearchOption.AllDirectories).FirstOrDefault();
            if (r.ExitCode != 0 || inf == null)
            {
                d.NotExportedReason = $"pnputil /export-driver a échoué (code {r.ExitCode}) : {r.Output}";
                log.Error($"Pilote « {d.Name} » : {d.NotExportedReason}");
                Directory.Delete(dest, true);
                return;
            }
            d.Exported = true;
            d.PackageFolder = folder;
            d.PackageInf = Path.GetRelativePath(staging, inf).Replace('\\', '/');
            d.InfArchitectures = DriverReader.ReadInfArchitectures(inf);
            int count = Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories).Count();
            log.Info($"Pilote « {d.Name} » {d.Version} : package {d.PublishedInf} exporté ({count} fichiers, architectures {string.Join("/", d.InfArchitectures)}).");
        }
        catch (Exception ex)
        {
            d.NotExportedReason = ex.Message;
            log.Error($"Pilote « {d.Name} » : {ex.Message}");
        }
    }

    private static string ExportReportText(Manifest m, InventoryDocument doc)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"DataBridge {m.ToolVersion} — export du {m.ExportedAt:dd/MM/yyyy HH:mm}");
        sb.AppendLine($"Source : {m.Source.ComputerName}, {m.Source.OsProductName} {m.Source.OsDisplayVersion} (build {m.Source.OsBuild}.{m.Source.OsUbr}), {m.Source.Architecture}, utilisateur {m.Source.UserName}");
        sb.AppendLine();
        foreach (var p in doc.Printers)
        {
            sb.AppendLine($"[{p.Id}] {p.Name} — {Labels.Kind(p.Kind)} — {Labels.Support(p.Support)}");
            foreach (var c in p.Components) sb.AppendLine($"    {Labels.State(c.State),-12} {c.Component} : {c.Detail}");
            foreach (var w in p.Warnings) sb.AppendLine($"    ! {w}");
            sb.AppendLine();
        }
        sb.AppendLine("Non couverts par cette sauvegarde : logiciels de numérisation, utilitaires constructeur, applications Microsoft Store associées,");
        sb.AppendLine("réglages que le constructeur enregistre hors des mécanismes d'impression de Windows.");
        return sb.ToString();
    }

    private static void WriteText(string root, string rel, string text) =>
        WriteBytes(root, rel, new System.Text.UTF8Encoding(false).GetBytes(text));

    private static void WriteBytes(string root, string rel, byte[] bytes)
    {
        string path = Path.Combine(root, rel.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static T Clone<T>(T value) => Json.Deserialize<T>(Json.Serialize(value))!;
}
