using System.Security.AccessControl;
using System.Security.Principal;
using DataBridge.Printers.Inventory;
using DataBridge.Printers.Model;

namespace DataBridge.Printers.Restore;

public sealed class SecurityPlan
{
    /// <summary>SDDL (DACL only) to apply, or null to keep the default permissions Windows gives the new queue.</summary>
    public string? Sddl { get; set; }
    public List<string> Notes { get; } = new();
}

/// <summary>
/// Translates the source DACL for the target PC. Identities that cannot be resolved are never replaced by a
/// broader group: an unresolvable "allow" entry is dropped (fewer rights); an unresolvable "deny" entry
/// cancels the whole ACL (dropping a deny would widen the rights) and Windows' defaults are kept.
/// </summary>
public static class SecurityPlanner
{
    public static SecurityPlan Build(SecurityInfo source, bool mapLocalAccountsByName)
    {
        var plan = new SecurityPlan();
        if (source.HasUnsupportedAces)
        {
            plan.Notes.Add("Les permissions d'origine contiennent des entrées d'un type non pris en charge : Windows gardera ses permissions par défaut pour cette imprimante.");
            return plan;
        }

        var acl = new RawAcl(GenericAcl.AclRevision, source.Aces.Count);
        int index = 0;
        foreach (var ace in source.Aces)
        {
            string label = ace.Account ?? ace.Sid;
            SecurityIdentifier? sid = null;
            switch (ace.Portability)
            {
                case SidPortability.WellKnown:
                    sid = new SecurityIdentifier(ace.Sid);
                    break;
                case SidPortability.Domain:
                    var domainSid = new SecurityIdentifier(ace.Sid);
                    if (SecurityReader.TryTranslate(domainSid) != null) sid = domainSid;
                    break;
                case SidPortability.LocalAccount:
                    if (mapLocalAccountsByName && ace.Account != null)
                    {
                        string user = ace.Account.Contains('\\') ? ace.Account[(ace.Account.IndexOf('\\') + 1)..] : ace.Account;
                        sid = SecurityReader.TryResolve($"{Environment.MachineName}\\{user}");
                        if (sid != null) plan.Notes.Add($"Compte local « {ace.Account} » associé au compte « {Environment.MachineName}\\{user} » de ce PC.");
                    }
                    break;
            }

            if (sid == null)
            {
                if (ace.IsDeny)
                {
                    plan.Notes.Clear();
                    plan.Notes.Add($"Une interdiction vise une identité introuvable sur ce PC (« {label} ») : pour ne pas élargir les droits, les permissions d'origine ne sont pas appliquées et Windows garde ses permissions par défaut.");
                    plan.Sddl = null;
                    return plan;
                }
                string note = ace.Portability == SidPortability.LocalAccount && !mapLocalAccountsByName
                    ? $"Droit accordé au compte local « {label} » du PC source non transféré : un compte local n'est pas le même d'un PC à l'autre (activez l'association par nom si le compte du même nom doit le recevoir)."
                    : $"Droit accordé à « {label} » non transféré : identité introuvable sur ce PC.";
                if (!plan.Notes.Contains(note)) plan.Notes.Add(note);
                continue;
            }
            acl.InsertAce(index++, new CommonAce((AceFlags)ace.AceFlags, ace.IsDeny ? AceQualifier.AccessDenied : AceQualifier.AccessAllowed,
                ace.AccessMask, sid, false, null));
        }
        var sd = new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent, null, null, null, acl);
        plan.Sddl = sd.GetSddlForm(AccessControlSections.Access);
        return plan;
    }

    /// <summary>Order-insensitive comparison of two DACLs.</summary>
    public static bool SameAccess(string sddlA, string sddlB)
    {
        static HashSet<string> Aces(string sddl)
        {
            var sd = new RawSecurityDescriptor(sddl);
            var set = new HashSet<string>();
            if (sd.DiscretionaryAcl == null) return set;
            foreach (GenericAce ace in sd.DiscretionaryAcl)
                if (ace is CommonAce ca) set.Add($"{ca.AceQualifier}|{ca.SecurityIdentifier.Value}|{ca.AccessMask}|{(byte)ca.AceFlags}");
            return set;
        }
        try { return Aces(sddlA).SetEquals(Aces(sddlB)); }
        catch { return false; }
    }
}
