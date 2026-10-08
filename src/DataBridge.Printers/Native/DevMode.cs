using System.Runtime.InteropServices;
using System.Text;
using DataBridge.Printers.Model;

namespace DataBridge.Printers.Native;

/// <summary>
/// DEVMODEW reader/writer working on raw bytes (wingdi.h layout, 220-byte public part followed by
/// dmDriverExtra bytes of driver-private data). Only the documented public fields are interpreted;
/// the private part is opaque and specific to the driver and its version.
/// </summary>
public sealed class DevMode
{
    // dmFields flags
    public const uint DM_ORIENTATION = 0x00000001;
    public const uint DM_PAPERSIZE = 0x00000002;
    public const uint DM_PAPERLENGTH = 0x00000004;
    public const uint DM_PAPERWIDTH = 0x00000008;
    public const uint DM_SCALE = 0x00000010;
    public const uint DM_NUP = 0x00000040;
    public const uint DM_COPIES = 0x00000100;
    public const uint DM_DEFAULTSOURCE = 0x00000200;
    public const uint DM_PRINTQUALITY = 0x00000400;
    public const uint DM_COLOR = 0x00000800;
    public const uint DM_DUPLEX = 0x00001000;
    public const uint DM_YRESOLUTION = 0x00002000;
    public const uint DM_COLLATE = 0x00008000;
    public const uint DM_FORMNAME = 0x00010000;
    public const uint DM_MEDIATYPE = 0x02000000;

    private const int OffDeviceName = 0, OffSpecVersion = 64, OffDriverVersion = 66, OffSize = 68, OffDriverExtra = 70, OffFields = 72;
    private const int OffOrientation = 76, OffPaperSize = 78, OffPaperLength = 80, OffPaperWidth = 82, OffScale = 84, OffCopies = 86;
    private const int OffDefaultSource = 88, OffPrintQuality = 90, OffColor = 92, OffDuplex = 94, OffYResolution = 96, OffCollate = 100;
    private const int OffFormName = 102, OffNup = 180, OffMediaType = 196;
    public const int MinimumSize = 156; // up to and including dmFormName

    public byte[] Bytes { get; }

    public DevMode(byte[] bytes)
    {
        if (bytes.Length < MinimumSize) throw new ArgumentException("DEVMODE trop court.");
        Bytes = bytes;
        if (Size < MinimumSize || Size + DriverExtra > bytes.Length) throw new ArgumentException("DEVMODE incohérent (taille déclarée).");
    }

    public static bool TryParse(byte[]? bytes, out DevMode? devMode)
    {
        devMode = null;
        if (bytes == null) return false;
        try { devMode = new DevMode(bytes); return true; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>Copies a DEVMODE from unmanaged memory (dmSize + dmDriverExtra bytes).</summary>
    internal static byte[]? CopyFromPointer(IntPtr p)
    {
        if (p == IntPtr.Zero) return null;
        ushort size = (ushort)Marshal.ReadInt16(p, OffSize);
        ushort extra = (ushort)Marshal.ReadInt16(p, OffDriverExtra);
        if (size < MinimumSize) return null;
        var bytes = new byte[size + extra];
        Marshal.Copy(p, bytes, 0, bytes.Length);
        return bytes;
    }

    public string DeviceName => ReadString(OffDeviceName, 32);
    public ushort SpecVersion => U16(OffSpecVersion);
    public ushort DriverVersion => U16(OffDriverVersion);
    public ushort Size => U16(OffSize);
    public ushort DriverExtra => U16(OffDriverExtra);
    public uint Fields
    {
        get => BitConverter.ToUInt32(Bytes, OffFields);
        set => BitConverter.TryWriteBytes(Bytes.AsSpan(OffFields), value);
    }

    public byte[] PrivatePart => Bytes.AsSpan(Size, DriverExtra).ToArray();

    /// <summary>Copy without the driver-private part (dmDriverExtra = 0).</summary>
    public byte[] WithoutPrivatePart()
    {
        var copy = Bytes.AsSpan(0, Size).ToArray();
        BitConverter.TryWriteBytes(copy.AsSpan(OffDriverExtra), (ushort)0);
        return copy;
    }

    public void SetDeviceName(string name)
    {
        var span = Bytes.AsSpan(OffDeviceName, 64);
        span.Clear();
        string truncated = name.Length > 31 ? name[..31] : name;
        Encoding.Unicode.GetBytes(truncated).CopyTo(span);
    }

    private bool Covers(int offset, int length) => offset + length <= Size;
    private ushort U16(int off) => BitConverter.ToUInt16(Bytes, off);
    private short S16(int off) => BitConverter.ToInt16(Bytes, off);
    private void W16(int off, short v) => BitConverter.TryWriteBytes(Bytes.AsSpan(off), v);

    private string ReadString(int offset, int chars)
    {
        string s = Encoding.Unicode.GetString(Bytes, offset, chars * 2);
        int nul = s.IndexOf('\0');
        return nul >= 0 ? s[..nul] : s;
    }

    private bool Has(uint flag, int offset, int length) => (Fields & flag) != 0 && Covers(offset, length);

    /// <summary>Extracts the documented public settings. binName maps a dmDefaultSource value to the driver's bin name.</summary>
    public DevModeSummary ToSummary(Func<short, string?>? binName = null)
    {
        var s = new DevModeSummary
        {
            DriverVersionField = DriverVersion,
            DriverExtra = DriverExtra,
            Size = Size,
        };
        if (Has(DM_ORIENTATION, OffOrientation, 2)) s.Orientation = S16(OffOrientation);
        if (Has(DM_PAPERSIZE, OffPaperSize, 2)) s.PaperSize = S16(OffPaperSize);
        if (Has(DM_PAPERLENGTH, OffPaperLength, 2)) s.PaperLength = S16(OffPaperLength);
        if (Has(DM_PAPERWIDTH, OffPaperWidth, 2)) s.PaperWidth = S16(OffPaperWidth);
        if (Has(DM_SCALE, OffScale, 2)) s.Scale = S16(OffScale);
        if (Has(DM_COPIES, OffCopies, 2)) s.Copies = S16(OffCopies);
        if (Has(DM_DEFAULTSOURCE, OffDefaultSource, 2))
        {
            s.DefaultSource = S16(OffDefaultSource);
            s.DefaultSourceName = binName?.Invoke(s.DefaultSource.Value);
        }
        if (Has(DM_PRINTQUALITY, OffPrintQuality, 2)) s.PrintQuality = S16(OffPrintQuality);
        if (Has(DM_COLOR, OffColor, 2)) s.Color = S16(OffColor);
        if (Has(DM_DUPLEX, OffDuplex, 2)) s.Duplex = S16(OffDuplex);
        if (Has(DM_YRESOLUTION, OffYResolution, 2)) s.YResolution = S16(OffYResolution);
        if (Has(DM_COLLATE, OffCollate, 2)) s.Collate = S16(OffCollate);
        if (Has(DM_FORMNAME, OffFormName, 64)) s.FormName = ReadString(OffFormName, 32);
        if (Has(DM_NUP, OffNup, 4)) s.Nup = BitConverter.ToInt32(Bytes, OffNup);
        if (Has(DM_MEDIATYPE, OffMediaType, 4)) s.MediaType = BitConverter.ToInt32(Bytes, OffMediaType);
        return s;
    }

    /// <summary>
    /// Writes the public settings of <paramref name="s"/> into this DEVMODE (only fields covered by its size).
    /// binId maps a bin name to this driver's dmDefaultSource value. Returns the fields that could not be written.
    /// </summary>
    public List<string> ApplySummary(DevModeSummary s, Func<string, short?>? binId = null)
    {
        var skipped = new List<string>();
        uint fields = Fields;

        void Set16(string label, short? value, uint flag, int off)
        {
            if (value == null) return;
            if (!Covers(off, 2)) { skipped.Add(label); return; }
            W16(off, value.Value);
            fields |= flag;
        }

        Set16("Orientation", s.Orientation, DM_ORIENTATION, OffOrientation);
        Set16("Format du papier", s.PaperSize, DM_PAPERSIZE, OffPaperSize);
        if (s.PaperSize == 256 /* DMPAPER_USER */)
        {
            Set16("Longueur du papier", s.PaperLength, DM_PAPERLENGTH, OffPaperLength);
            Set16("Largeur du papier", s.PaperWidth, DM_PAPERWIDTH, OffPaperWidth);
        }
        Set16("Échelle", s.Scale, DM_SCALE, OffScale);
        Set16("Copies", s.Copies, DM_COPIES, OffCopies);
        Set16("Qualité", s.PrintQuality, DM_PRINTQUALITY, OffPrintQuality);
        Set16("Couleur", s.Color, DM_COLOR, OffColor);
        Set16("Recto verso", s.Duplex, DM_DUPLEX, OffDuplex);
        Set16("Résolution", s.YResolution, DM_YRESOLUTION, OffYResolution);
        Set16("Assemblage", s.Collate, DM_COLLATE, OffCollate);

        if (s.DefaultSource != null)
        {
            short? id = null;
            if (s.DefaultSourceName != null && binId != null) id = binId(s.DefaultSourceName);
            // Standard DMBIN_* values (< 256) are driver independent; values >= 256 are only meaningful by name.
            if (id == null && s.DefaultSource.Value < 256 && s.DefaultSourceName == null) id = s.DefaultSource.Value;
            if (id != null && Covers(OffDefaultSource, 2))
            {
                W16(OffDefaultSource, id.Value);
                fields |= DM_DEFAULTSOURCE;
            }
            else skipped.Add($"Bac « {s.DefaultSourceName ?? s.DefaultSource.ToString()} » (absent du pilote cible)");
        }

        if (s.FormName != null)
        {
            if (Covers(OffFormName, 64))
            {
                var span = Bytes.AsSpan(OffFormName, 64);
                span.Clear();
                Encoding.Unicode.GetBytes(s.FormName.Length > 31 ? s.FormName[..31] : s.FormName).CopyTo(span);
                fields |= DM_FORMNAME;
            }
            else skipped.Add("Nom du format");
        }
        if (s.Nup != null)
        {
            if (Covers(OffNup, 4)) { BitConverter.TryWriteBytes(Bytes.AsSpan(OffNup), s.Nup.Value); fields |= DM_NUP; }
            else skipped.Add("Pages par feuille");
        }
        if (s.MediaType != null && s.MediaType.Value < 256)
        {
            // DMMEDIA_* above 256 are driver specific: only standard media types are copied by value.
            if (Covers(OffMediaType, 4)) { BitConverter.TryWriteBytes(Bytes.AsSpan(OffMediaType), s.MediaType.Value); fields |= DM_MEDIATYPE; }
            else skipped.Add("Type de support");
        }
        else if (s.MediaType != null) skipped.Add("Type de support (valeur propre au pilote)");

        Fields = fields;
        return skipped;
    }
}
