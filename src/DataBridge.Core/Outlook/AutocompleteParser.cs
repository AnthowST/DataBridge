// Moteur Outlook repris de Sauvegarde Outlook (même format de PST et mêmes propriétés de dossier :
// une sauvegarde faite par DataBridge peut aussi être restaurée avec Sauvegarde Outlook, et inversement).
#nullable disable
using System.Text;

namespace DataBridge.Core.Outlook;

/// <summary>
/// Lecture du flux d'autocomplétion d'Outlook 2010+ (même structure que les anciens fichiers .nk2).
/// Le décodage sert uniquement à produire une liste lisible : la sauvegarde de référence
/// est toujours la copie brute du flux.
/// </summary>
public static class AutocompleteParser
{
    public sealed record Entry(string Name, string Email);

    const uint Signature = 0xBAADF00D;

    public static int? CountRows(byte[] data)
    {
        if (data == null || data.Length < 16 || BitConverter.ToUInt32(data, 0) != Signature) return null;
        return (int)BitConverter.ToUInt32(data, 12);
    }

    /// <summary>
    /// Décode les entrées. Lève une exception si l'en-tête est invalide ; si une entrée est illisible,
    /// renvoie les entrées décodées jusque-là avec <paramref name="complete"/> = false.
    /// </summary>
    public static List<Entry> Parse(byte[] data, out bool complete)
    {
        using var br = new BinaryReader(new MemoryStream(data));
        if (br.ReadUInt32() != Signature) throw new InvalidDataException("Signature inconnue.");
        br.ReadUInt32(); // version majeure
        br.ReadUInt32(); // version mineure
        uint rows = br.ReadUInt32();
        if (rows > 1_000_000) throw new InvalidDataException("Nombre de lignes incohérent.");

        var list = new List<Entry>();
        complete = false;
        try
        {
            for (uint r = 0; r < rows; r++) list.Add(ReadRow(br));
            complete = true;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException) { }
        return list;
    }

    static Entry ReadRow(BinaryReader br)
    {
        uint propCount = br.ReadUInt32();
        if (propCount > 1000) throw new InvalidDataException("Nombre de propriétés incohérent.");
        string display = null, email = null, smtp = null;

        for (uint p = 0; p < propCount; p++)
        {
            uint tag = br.ReadUInt32();
            br.ReadUInt32();     // réservé
            br.ReadBytes(8);     // valeur (union)
            ushort type = (ushort)(tag & 0xFFFF);

            if ((type & 0x1000) != 0)
            {
                // Multivaluée : nombre de valeurs, puis chaque valeur (longueur + données si variable).
                ushort baseType = (ushort)(type & 0x0FFF);
                int count = br.ReadInt32();
                if (count < 0 || count > 10_000) throw new InvalidDataException("Nombre de valeurs incohérent.");
                for (int k = 0; k < count; k++)
                {
                    if (IsVariable(baseType)) ReadVariable(br);
                    else br.ReadBytes(FixedSize(baseType));
                }
                continue;
            }

            if (IsVariable(type))
            {
                byte[] bytes = ReadVariable(br);
                if (type == 0x0102) continue;
                string s = (type == 0x001F ? Encoding.Unicode : Encoding.Latin1).GetString(bytes).TrimEnd('\0');
                switch (tag >> 16)
                {
                    case 0x3001: display = s; break; // PR_DISPLAY_NAME
                    case 0x3003: email = s; break;   // PR_EMAIL_ADDRESS
                    case 0x39FE: smtp = s; break;    // PR_SMTP_ADDRESS
                }
            }
            else if (type == 0x0048) br.ReadBytes(16); // PT_CLSID
            else FixedSize(type);                      // valeur fixe contenue dans l'union (valide le type)
        }

        string address = !string.IsNullOrWhiteSpace(smtp) ? smtp : email ?? "";
        return new Entry(display ?? "", address);
    }

    static bool IsVariable(ushort t) => t is 0x001E or 0x001F or 0x0102;

    static byte[] ReadVariable(BinaryReader br)
    {
        int len = br.ReadInt32();
        if (len < 0 || len > br.BaseStream.Length - br.BaseStream.Position) throw new InvalidDataException("Longueur incohérente.");
        return br.ReadBytes(len);
    }

    static int FixedSize(ushort t) => t switch
    {
        0x0001 => 0,
        0x0002 or 0x000B => 2,
        0x0003 or 0x0004 or 0x000A => 4,
        0x0005 or 0x0006 or 0x0007 or 0x0014 or 0x0040 => 8,
        0x0048 => 16,
        _ => throw new InvalidDataException($"Type de propriété inconnu 0x{t:X4}."),
    };
}
