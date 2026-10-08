using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using DataBridge.Printers.Inventory;
using DataBridge.Printers.Model;
using DataBridge.Printers.Native;
using DataBridge.Printers.Restore;

namespace DataBridge.Tests.Printers;

public class DevModeTests
{
    private static byte[] Sample(int driverExtra = 16)
    {
        var b = new byte[220 + driverExtra];
        Encoding.Unicode.GetBytes("Imprimante").CopyTo(b, 0);
        BitConverter.TryWriteBytes(b.AsSpan(64), (ushort)0x0401);
        BitConverter.TryWriteBytes(b.AsSpan(68), (ushort)220);
        BitConverter.TryWriteBytes(b.AsSpan(70), (ushort)driverExtra);
        uint fields = DevMode.DM_ORIENTATION | DevMode.DM_PAPERSIZE | DevMode.DM_COLOR | DevMode.DM_DUPLEX | DevMode.DM_DEFAULTSOURCE | DevMode.DM_COPIES | DevMode.DM_FORMNAME;
        BitConverter.TryWriteBytes(b.AsSpan(72), fields);
        BitConverter.TryWriteBytes(b.AsSpan(76), (short)2);   // landscape
        BitConverter.TryWriteBytes(b.AsSpan(78), (short)9);   // A4
        BitConverter.TryWriteBytes(b.AsSpan(86), (short)3);   // copies
        BitConverter.TryWriteBytes(b.AsSpan(88), (short)260); // driver-specific bin
        BitConverter.TryWriteBytes(b.AsSpan(92), (short)1);   // monochrome
        BitConverter.TryWriteBytes(b.AsSpan(94), (short)2);   // long edge
        Encoding.Unicode.GetBytes("A4").CopyTo(b, 102);
        for (int i = 0; i < driverExtra; i++) b[220 + i] = (byte)(i + 1);
        return b;
    }

    [Fact]
    public void Summary_ReadsPublicFields_AndBinByName()
    {
        var dm = new DevMode(Sample());
        var s = dm.ToSummary(id => id == 260 ? "Bac 2" : null);
        Assert.Equal((short)2, s.Orientation);
        Assert.Equal((short)9, s.PaperSize);
        Assert.Equal("A4", s.FormName);
        Assert.Equal((short)1, s.Color);
        Assert.Equal((short)2, s.Duplex);
        Assert.Equal((short)3, s.Copies);
        Assert.Equal("Bac 2", s.DefaultSourceName);
        Assert.Null(s.PrintQuality); // not in dmFields
        Assert.Equal(16, dm.PrivatePart.Length);
    }

    [Fact]
    public void Apply_MapsDriverSpecificBinByName_AndReportsMissingBin()
    {
        var source = new DevMode(Sample()).ToSummary(id => id == 260 ? "Bac 2" : null);
        var target = new DevMode(Sample());
        BitConverter.TryWriteBytes(target.Bytes.AsSpan(72), 0u); // empty dmFields
        var skipped = target.ApplySummary(source, name => name == "Bac 2" ? (short)263 : null);
        Assert.Empty(skipped);
        var after = target.ToSummary(id => id == 263 ? "Bac 2" : null);
        Assert.Equal("Bac 2", after.DefaultSourceName);
        Assert.Equal((short)2, after.Duplex);

        var target2 = new DevMode(Sample());
        var skipped2 = target2.ApplySummary(source, _ => null);
        Assert.Contains(skipped2, s => s.Contains("Bac 2"));
    }

    [Fact]
    public void WithoutPrivatePart_ClearsDriverExtra()
    {
        var stripped = new DevMode(new DevMode(Sample()).WithoutPrivatePart());
        Assert.Equal(0, stripped.DriverExtra);
        Assert.Equal(220, stripped.Bytes.Length);
    }

    [Fact]
    public void InconsistentSize_IsRejected()
    {
        var b = Sample();
        BitConverter.TryWriteBytes(b.AsSpan(70), (ushort)5000);
        Assert.False(DevMode.TryParse(b, out _));
        Assert.False(DevMode.TryParse(new byte[10], out _));
    }

    [Fact]
    public void DeviceName_IsTruncatedTo31Chars()
    {
        var dm = new DevMode(Sample());
        dm.SetDeviceName(new string('é', 50));
        Assert.Equal(31, dm.DeviceName.Length);
    }
}

public class PortTests
{
    [Theory]
    [InlineData("IP_10.0.0.1", "Standard TCP/IP Port", PortKind.StandardTcpIp)]
    [InlineData("IP_10.0.0.1", "TCPMON.DLL", PortKind.StandardTcpIp)]
    [InlineData("WSD-1234-5678", "WSD Port", PortKind.Wsd)]
    [InlineData("USB001", "USB Monitor", PortKind.Usb)]
    [InlineData("PORTPROMPT:", "Local Port", PortKind.Virtual)]
    [InlineData("LPT1:", "Local Port", PortKind.Local)]
    [InlineData("http://printer/ipp", "", PortKind.Ipp)]
    // A port named like an IP address but served by another monitor is NOT a TCP/IP port.
    [InlineData("192.168.1.50", "WSD Port", PortKind.Wsd)]
    public void Classify_UsesMonitorNotName(string name, string monitor, PortKind expected) =>
        Assert.Equal(expected, PortReader.Classify(name, monitor));

    [Fact]
    public void SameConfiguration_ComparesLprQueue()
    {
        var a = new PortEntry { Kind = PortKind.StandardTcpIp, Protocol = "LPR", HostAddress = "10.0.0.5", PortNumber = 515, LprQueue = "BINARY_P1" };
        var b = new PortEntry { Kind = PortKind.StandardTcpIp, Protocol = "LPR", HostAddress = "10.0.0.5", PortNumber = 515, LprQueue = "TEXT_P1" };
        Assert.False(a.SameConfigurationAs(b));
        b.LprQueue = "BINARY_P1";
        Assert.True(a.SameConfigurationAs(b));
        b.HostAddress = "10.0.0.6";
        Assert.False(a.SameConfigurationAs(b));
    }
}

public class DriverTests
{
    [Fact]
    public void InfArchitectures_AreParsed()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".inf");
        File.WriteAllText(path, "[Version]\nSignature=\"$Windows NT$\"\n\n[Manufacturer]\n%Company% = DriverName,NTx86,NTamd64.10.0 ; commentaire\n\n[DriverName.NTamd64.10.0]\n\"Modèle Test\" = x\n", Encoding.Unicode);
        try
        {
            var arch = DriverReader.ReadInfArchitectures(path);
            Assert.Equal(new[] { "NTx86", "NTamd64" }, arch);
            Assert.True(DriverReader.SupportsEnvironment(arch, "Windows x64"));
            Assert.False(DriverReader.SupportsEnvironment(arch, "Windows ARM64"));
            Assert.True(DriverReader.InfMentionsModel(path, "Modèle Test"));
            Assert.False(DriverReader.InfMentionsModel(path, "Autre modèle"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void VersionFormatting() => Assert.Equal("1.7.0.0", DriverReader.FormatVersion(281505041481728));
}

public class SecurityTests
{
    private static AceInfo Ace(string sid, bool deny, SidPortability p, string? account = null) =>
        new() { Sid = sid, IsDeny = deny, Portability = p, AccessMask = 0x20008, Account = account };

    [Fact]
    public void WellKnownEntries_AreKept()
    {
        var info = new SecurityInfo { Aces = { Ace("S-1-1-0", false, SidPortability.WellKnown), Ace("S-1-5-32-544", false, SidPortability.WellKnown) } };
        var plan = SecurityPlanner.Build(info, false);
        Assert.NotNull(plan.Sddl);
        Assert.Contains("WD", plan.Sddl);
        Assert.Contains("BA", plan.Sddl);
        Assert.Empty(plan.Notes);
    }

    [Fact]
    public void UnresolvableAllow_IsDropped_NeverWidened()
    {
        var info = new SecurityInfo { Aces = { Ace("S-1-5-32-544", false, SidPortability.WellKnown), Ace("S-1-5-21-1-2-3-1001", false, SidPortability.Unresolved, "AUTREPC\\bob") } };
        var plan = SecurityPlanner.Build(info, false);
        Assert.NotNull(plan.Sddl);
        Assert.DoesNotContain("1001", plan.Sddl);
        Assert.DoesNotContain("WD", plan.Sddl); // never replaced by Everyone
        Assert.Single(plan.Notes);
    }

    [Fact]
    public void UnresolvableDeny_CancelsTheWholeAcl()
    {
        var info = new SecurityInfo { Aces = { Ace("S-1-1-0", false, SidPortability.WellKnown), Ace("S-1-5-21-1-2-3-1002", true, SidPortability.Unresolved, "AUTREPC\\stagiaire") } };
        var plan = SecurityPlanner.Build(info, false);
        Assert.Null(plan.Sddl);
        Assert.Contains(plan.Notes, n => n.Contains("stagiaire"));
    }

    [Fact]
    public void UnsupportedAceTypes_KeepWindowsDefaults()
    {
        var plan = SecurityPlanner.Build(new SecurityInfo { HasUnsupportedAces = true }, false);
        Assert.Null(plan.Sddl);
    }

    [Fact]
    public void SameAccess_IgnoresOrder()
    {
        Assert.True(SecurityPlanner.SameAccess("D:(A;;GA;;;BA)(A;;SWRC;;;WD)", "D:(A;;SWRC;;;WD)(A;;GA;;;BA)"));
        Assert.False(SecurityPlanner.SameAccess("D:(A;;GA;;;BA)", "D:(A;;GA;;;BA)(A;;SWRC;;;WD)"));
    }

    [Fact]
    public void Analyze_ClassifiesBuiltinGroups()
    {
        var sd = new RawSecurityDescriptor("D:(A;;GA;;;BA)(A;;SWRC;;;WD)");
        var bytes = new byte[sd.BinaryLength];
        sd.GetBinaryForm(bytes, 0);
        var info = SecurityReader.Analyze(bytes);
        Assert.Equal(2, info.Aces.Count);
        Assert.All(info.Aces, a => Assert.Equal(SidPortability.WellKnown, a.Portability));
        Assert.False(info.HasNonPortableEntries);
    }

    [Fact]
    public void Analyze_FlagsCurrentLocalUser()
    {
        using var id = WindowsIdentity.GetCurrent();
        if (!string.Equals(Environment.UserDomainName, Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return; // domain account: not applicable
        var sd = new RawSecurityDescriptor($"D:(A;;GA;;;{id.User!.Value})");
        var bytes = new byte[sd.BinaryLength];
        sd.GetBinaryForm(bytes, 0);
        var info = SecurityReader.Analyze(bytes);
        Assert.Equal(SidPortability.LocalAccount, info.Aces[0].Portability);
        Assert.True(info.HasNonPortableEntries);
    }
}

public class PlanTests
{
    private static PrinterPlan Plan(PrinterKind kind = PrinterKind.TcpIp) => new()
    {
        Entry = new PrinterEntry { Name = "Accueil", Kind = kind, Support = SupportLevel.Supported, DriverName = "Pilote X" },
        Port = new PortEntry { Name = "IP_1", Kind = PortKind.StandardTcpIp, Protocol = "RAW", HostAddress = "10.0.0.1", PortNumber = 9100 },
        Driver = new DriverEntry { Name = "Pilote X", Version = "2.0.0.0" },
    };

    [Fact]
    public void RenameWithoutName_IsBlocking()
    {
        var p = Plan();
        p.NameAction = NameAction.CreateWithOtherName;
        p.PortAction = PortAction.Create;
        p.DriverAction = DriverAction.UseInstalled;
        p.NewName = "";
        Assert.False(p.CanRestore);
        p.NewName = "Accueil (2)";
        Assert.True(p.CanRestore);
        Assert.Equal("Accueil (2)", p.TargetName);
    }

    [Fact]
    public void PortReconfigure_UsedByOthers_IsBlocking()
    {
        var p = Plan();
        p.PortAction = PortAction.Reconfigure;
        p.DriverAction = DriverAction.UseInstalled;
        p.PortUsedBy = new() { "Autre imprimante" };
        Assert.False(p.CanRestore);
    }

    [Fact]
    public void MissingDriver_IsBlocking_UntilPackageChosen()
    {
        var p = Plan();
        p.PortAction = PortAction.Create;
        p.DriverAction = DriverAction.None;
        Assert.False(p.CanRestore);
        p.DriverAction = DriverAction.InstallUserPackage;
        Assert.False(p.CanRestore);
        p.UserInfPath = @"C:\pilotes\x.inf";
        Assert.True(p.CanRestore);
    }

    [Fact]
    public void DriverUpgrade_WarnsAboutOtherPrinters()
    {
        var p = Plan();
        p.PortAction = PortAction.Create;
        p.DriverAction = DriverAction.InstallFromArchive;
        p.DriverVersionDiffers = true;
        p.InstalledDriverVersion = "1.0.0.0";
        p.DriverUsedBy = new() { "Compta", "RH" };
        Assert.True(p.CanRestore);
        Assert.Contains(p.Issues, i => i.Level == IssueLevel.Warning && i.Text.Contains("Compta"));
    }

    [Fact]
    public void UnsupportedPrinter_CannotBeRestored()
    {
        var p = Plan(PrinterKind.Usb);
        p.Entry.Support = SupportLevel.DetectedOnly;
        Assert.False(p.CanRestore);
    }

    [Fact]
    public void LocalQueueNeedsAdmin_ConnectionDoesNot()
    {
        var local = Plan();
        local.Selected = true;
        local.DriverAction = DriverAction.UseInstalled;
        Assert.True(local.RequiresAdmin);
        var conn = Plan(PrinterKind.SharedConnection);
        conn.Selected = true;
        conn.DriverAction = DriverAction.FromServer;
        Assert.False(conn.RequiresAdmin);
        conn.DriverAction = DriverAction.InstallFromArchive;
        Assert.True(conn.RequiresAdmin);
    }
}
