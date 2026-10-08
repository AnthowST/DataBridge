using System.Text;
using System.Text.RegularExpressions;
using DataBridge.Printers.Model;
using DataBridge.Printers.Native;
using DataBridge.Printers.Util;
using static DataBridge.Printers.Native.NativeMethods;

namespace DataBridge.Printers.Inventory;

public static class DriverReader
{
    public static List<DriverEntry> ReadAll(string environment, bool includeSigner = true)
    {
        var published = PublishedInfMap();
        var packages = includeSigner ? PnpUtil.EnumPrinterPackages() : null;
        var result = new List<DriverEntry>();
        foreach (var d in Spooler.EnumDrivers8(environment))
            result.Add(Build(d, published, packages));
        return result;
    }

    public static DriverEntry? Find(string name, string environment) =>
        ReadAll(environment, includeSigner: false).FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

    private static DriverEntry Build(DRIVER_INFO_8 d, Dictionary<string, string> published, List<PnpUtil.DriverPackageInfo>? packages)
    {
        var e = new DriverEntry
        {
            Name = d.pName ?? "",
            Environment = d.pEnvironment ?? "",
            MajorVersion = d.cVersion,
            Manufacturer = d.pszMfgName,
            Provider = d.pszProvider,
            VersionRaw = d.dwlDriverVersion,
            Version = FormatVersion(d.dwlDriverVersion),
            DriverDate = d.ftDriverDate.ToDateTime(),
            HardwareId = d.pszHardwareID,
            PrintProcessor = d.pszPrintProcessor,
            InfPath = d.pszInfPath,
            InfOriginalName = string.IsNullOrEmpty(d.pszInfPath) ? null : Path.GetFileName(d.pszInfPath),
        };
        if (!string.IsNullOrEmpty(e.InfPath) && published.TryGetValue(NormalizePath(e.InfPath), out var oem))
        {
            e.PublishedInf = oem;
            e.Signer = packages?.FirstOrDefault(p => string.Equals(p.PublishedName, oem, StringComparison.OrdinalIgnoreCase))?.Signer;
        }
        // A package that was not published as oemNN.inf ships with Windows (prnms*.inf, ntprint.inf…).
        // Windows optional features (Print to PDF…) are published as oemNN.inf but signed "Microsoft Windows".
        e.IsInbox = !string.IsNullOrEmpty(e.InfPath)
            && (e.PublishedInf == null || string.Equals(e.Signer, "Microsoft Windows", StringComparison.OrdinalIgnoreCase));
        return e;
    }

    public static string FormatVersion(ulong v) =>
        $"{(v >> 48) & 0xFFFF}.{(v >> 32) & 0xFFFF}.{(v >> 16) & 0xFFFF}.{v & 0xFFFF}";

    private static string NormalizePath(string p) => Path.GetFullPath(p).TrimEnd('\\').ToLowerInvariant();

    /// <summary>Driver store INF path → published name (oemNN.inf), for every third-party package.</summary>
    public static Dictionary<string, string> PublishedInfMap()
    {
        var map = new Dictionary<string, string>();
        string infDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
        foreach (var file in Directory.EnumerateFiles(infDir, "oem*.inf"))
        {
            string oem = Path.GetFileName(file);
            var sb = new StringBuilder(1024);
            if (SetupGetInfDriverStoreLocation(oem, IntPtr.Zero, null, sb, (uint)sb.Capacity, out _))
                map[NormalizePath(sb.ToString())] = oem;
        }
        return map;
    }

    /// <summary>Architecture decorations of the [Manufacturer] section (e.g. NTamd64, NTx86, NTarm64).</summary>
    public static List<string> ReadInfArchitectures(string infPath)
    {
        var result = new List<string>();
        string text = ReadInfText(infPath);
        bool inSection = false;
        foreach (var raw in text.Split('\n'))
        {
            string line = StripComment(raw).Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('['))
            {
                if (inSection) break;
                inSection = line.Equals("[Manufacturer]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection) continue;
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            var parts = line[(eq + 1)..].Split(',');
            foreach (var part in parts.Skip(1))
            {
                string deco = part.Trim();
                int dot = deco.IndexOf('.');
                string arch = dot > 0 ? deco[..dot] : deco;
                if (arch.Length > 0 && !result.Contains(arch, StringComparer.OrdinalIgnoreCase)) result.Add(arch);
            }
        }
        return result;
    }

    /// <summary>True when the INF text declares the given model name (quoted) — a quick pre-check before installation.</summary>
    public static bool InfMentionsModel(string infPath, string modelName)
    {
        string text = ReadInfText(infPath);
        return text.Contains("\"" + modelName + "\"", StringComparison.OrdinalIgnoreCase);
    }

    public static bool SupportsEnvironment(IEnumerable<string> infArchitectures, string environment)
    {
        string needed = environment switch
        {
            "Windows x64" => "NTamd64",
            "Windows ARM64" => "NTarm64",
            "Windows NT x86" => "NTx86",
            _ => "NTamd64",
        };
        var list = infArchitectures.ToList();
        // An undecorated [Manufacturer] section means "all architectures" for the INF syntax, but print drivers
        // always contain native binaries: undecorated packages are treated as x86 only, to stay on the safe side.
        return list.Any(a => a.Equals(needed, StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadInfText(string infPath)
    {
        using var reader = new StreamReader(infPath, Encoding.Latin1, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string StripComment(string line)
    {
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inQuotes = !inQuotes;
            else if (line[i] == ';' && !inQuotes) return line[..i];
        }
        return line;
    }

    public static bool IsArchitectureName(string s) => Regex.IsMatch(s, "^NT(amd64|x86|arm64|ia64|arm)$", RegexOptions.IgnoreCase);
}
