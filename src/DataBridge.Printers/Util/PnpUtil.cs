using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

namespace DataBridge.Printers.Util;

/// <summary>Runs the inbox pnputil.exe (no shell, fixed arguments, absolute path).</summary>
public static class PnpUtil
{
    public static string ExePath => Path.Combine(Environment.SystemDirectory, "pnputil.exe");

    public sealed record Result(int ExitCode, string Output);

    public static Result Run(params string[] args)
    {
        var psi = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("pnputil.exe n'a pas pu être lancé.");
        var stdErr = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(TimeSpan.FromMinutes(5)))
        {
            try { p.Kill(); } catch { }
            throw new TimeoutException("pnputil.exe ne répond pas.");
        }
        return new Result(p.ExitCode, (output + stdErr.Result).Trim());
    }

    public sealed record DriverPackageInfo(string PublishedName, string OriginalName, string? Provider, string? Version, string? Signer);

    /// <summary>
    /// Printer-class packages from "pnputil /enum-drivers /class Printer /format xml". The XML format is not
    /// available on every Windows 11 build: returns null in that case (signer then stays unknown).
    /// </summary>
    public static List<DriverPackageInfo>? EnumPrinterPackages()
    {
        try
        {
            var r = Run("/enum-drivers", "/class", "Printer", "/format", "xml");
            if (r.ExitCode != 0) return null;
            int start = r.Output.IndexOf("<?xml", StringComparison.Ordinal);
            if (start < 0) start = r.Output.IndexOf("<PnpUtil", StringComparison.Ordinal);
            if (start < 0) return null;
            var doc = XDocument.Parse(r.Output[start..]);
            return doc.Descendants("Driver").Select(d => new DriverPackageInfo(
                (string?)d.Attribute("DriverName") ?? "",
                (string?)d.Element("OriginalName") ?? "",
                (string?)d.Element("ProviderName"),
                (string?)d.Element("DriverVersion"),
                (string?)d.Element("SignerName"))).ToList();
        }
        catch { return null; }
    }
}
