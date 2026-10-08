using System.Security.AccessControl;
using System.Security.Principal;
using DataBridge.Printers.Model;

namespace DataBridge.Printers.Inventory;

public static class SecurityReader
{
    private static readonly Lazy<SecurityIdentifier?> MachineSid = new(ResolveMachineSid);

    public static SecurityInfo Analyze(byte[] selfRelativeSd)
    {
        var raw = new RawSecurityDescriptor(selfRelativeSd, 0);
        var info = new SecurityInfo { Sddl = raw.GetSddlForm(AccessControlSections.Access) };
        if (raw.DiscretionaryAcl == null) return info;
        foreach (GenericAce ace in raw.DiscretionaryAcl)
        {
            if (ace is CommonAce ca && (ca.AceQualifier == AceQualifier.AccessAllowed || ca.AceQualifier == AceQualifier.AccessDenied))
            {
                var a = new AceInfo
                {
                    Sid = ca.SecurityIdentifier.Value,
                    Account = TryTranslate(ca.SecurityIdentifier),
                    IsDeny = ca.AceQualifier == AceQualifier.AccessDenied,
                    AccessMask = ca.AccessMask,
                    AceFlags = (byte)ca.AceFlags,
                };
                a.Portability = Classify(ca.SecurityIdentifier, a.Account);
                if (a.Portability is SidPortability.LocalAccount or SidPortability.Unresolved) info.HasNonPortableEntries = true;
                info.Aces.Add(a);
            }
            else
            {
                info.HasUnsupportedAces = true;
            }
        }
        return info;
    }

    public static SidPortability Classify(SecurityIdentifier sid, string? account)
    {
        if (!sid.Value.StartsWith("S-1-5-21-", StringComparison.Ordinal)) return SidPortability.WellKnown;
        if (MachineSid.Value != null && sid.AccountDomainSid != null && sid.AccountDomainSid.Equals(MachineSid.Value)) return SidPortability.LocalAccount;
        return account == null ? SidPortability.Unresolved : SidPortability.Domain;
    }

    public static string? TryTranslate(SecurityIdentifier sid)
    {
        try { return sid.Translate(typeof(NTAccount)).Value; }
        catch { return null; }
    }

    public static SecurityIdentifier? TryResolve(string account)
    {
        try { return (SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier)); }
        catch { return null; }
    }

    private static SecurityIdentifier? ResolveMachineSid()
    {
        try
        {
            var sid = (SecurityIdentifier)new NTAccount(Environment.MachineName).Translate(typeof(SecurityIdentifier));
            return sid.AccountDomainSid ?? sid;
        }
        catch { }
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            if (string.Equals(Environment.UserDomainName, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                return id.User?.AccountDomainSid;
        }
        catch { }
        return null;
    }

    public static SecurityIdentifier? LocalMachineSid => MachineSid.Value;
}
