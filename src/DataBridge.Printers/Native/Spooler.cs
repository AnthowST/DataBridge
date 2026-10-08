using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using static DataBridge.Printers.Native.NativeMethods;

namespace DataBridge.Printers.Native;

/// <summary>Owned printer handle (ClosePrinter on dispose).</summary>
internal sealed class PrinterHandle : IDisposable
{
    public IntPtr Handle { get; private set; }
    public string Name { get; }

    private PrinterHandle(IntPtr handle, string name)
    {
        Handle = handle;
        Name = name;
    }

    public static PrinterHandle Open(string? name, uint access)
    {
        if (TryOpen(name, access, out var handle, out int error)) return handle!;
        throw new Win32Exception(error, $"Ouverture de « {name ?? "(serveur local)"} » impossible : {new Win32Exception(error).Message}");
    }

    public static bool TryOpen(string? name, uint access, out PrinterHandle? handle, out int error)
    {
        var defaults = new PRINTER_DEFAULTS { DesiredAccess = access };
        if (OpenPrinter(name, out IntPtr h, ref defaults))
        {
            handle = new PrinterHandle(h, name ?? "");
            error = 0;
            return true;
        }
        handle = null;
        error = Marshal.GetLastWin32Error();
        return false;
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            ClosePrinter(Handle);
            Handle = IntPtr.Zero;
        }
    }
}

/// <summary>Thin, exception-based helpers around the spooler API.</summary>
internal static class Spooler
{
    /// <summary>Calls a "size query, then fill" Win32 function and returns the filled buffer (caller frees).</summary>
    private static IntPtr Fill(Func<IntPtr, uint, (bool ok, uint needed)> call, string what)
    {
        var (ok, needed) = call(IntPtr.Zero, 0);
        if (ok && needed == 0) return IntPtr.Zero;
        int err = Marshal.GetLastWin32Error();
        if (!ok && err != ERROR_INSUFFICIENT_BUFFER && err != ERROR_MORE_DATA && needed == 0)
            throw new Win32Exception(err, $"{what} : {new Win32Exception(err).Message}");
        for (int attempt = 0; attempt < 4; attempt++)
        {
            IntPtr buffer = Marshal.AllocHGlobal((int)Math.Max(needed, 1));
            var (ok2, needed2) = call(buffer, needed);
            if (ok2) return buffer;
            err = Marshal.GetLastWin32Error();
            Marshal.FreeHGlobal(buffer);
            if (err != ERROR_INSUFFICIENT_BUFFER && err != ERROR_MORE_DATA)
                throw new Win32Exception(err, $"{what} : {new Win32Exception(err).Message}");
            needed = needed2;
        }
        throw new Win32Exception(ERROR_INSUFFICIENT_BUFFER, what);
    }

    public static List<T> Enumerate<T>(Func<IntPtr, uint, (bool ok, uint needed, uint count)> call, string what) where T : struct
    {
        uint count = 0;
        IntPtr buffer = Fill((buf, cb) =>
        {
            var r = call(buf, cb);
            count = r.count;
            return (r.ok, r.needed);
        }, what);
        var list = new List<T>();
        if (buffer == IntPtr.Zero) return list;
        try
        {
            int size = Marshal.SizeOf<T>();
            for (int i = 0; i < count; i++)
                list.Add(Marshal.PtrToStructure<T>(buffer + i * size));
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return list;
    }

    public static List<PRINTER_INFO_4> EnumPrinters4(uint flags) =>
        Enumerate<PRINTER_INFO_4>((b, cb) => (EnumPrinters(flags, null, 4, b, cb, out var n, out var c), n, c), "Énumération des imprimantes");

    public static List<PRINTER_INFO_2> EnumPrintersLocal2() =>
        Enumerate<PRINTER_INFO_2>((b, cb) => (EnumPrinters(PRINTER_ENUM_LOCAL, null, 2, b, cb, out var n, out var c), n, c), "Énumération des imprimantes");

    public static List<PORT_INFO_2> EnumPorts2() =>
        Enumerate<PORT_INFO_2>((b, cb) => (EnumPorts(null, 2, b, cb, out var n, out var c), n, c), "Énumération des ports");

    public static List<DRIVER_INFO_8> EnumDrivers8(string environment) =>
        Enumerate<DRIVER_INFO_8>((b, cb) => (EnumPrinterDrivers(null, environment, 8, b, cb, out var n, out var c), n, c), "Énumération des pilotes");

    /// <summary>Reads PRINTER_INFO_2. The DEVMODE is copied out (null if absent).</summary>
    public static (PRINTER_INFO_2 info, byte[]? devMode) GetPrinter2(PrinterHandle h)
    {
        IntPtr buffer = Fill((b, cb) => (GetPrinter(h.Handle, 2, b, cb, out var n), n), $"Lecture de « {h.Name} »");
        try
        {
            var info = Marshal.PtrToStructure<PRINTER_INFO_2>(buffer);
            byte[]? dm = DevMode.CopyFromPointer(info.pDevMode);
            info.pDevMode = IntPtr.Zero;
            info.pSecurityDescriptor = IntPtr.Zero;
            return (info, dm);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>Reads PRINTER_INFO_8 (global DEVMODE) or PRINTER_INFO_9 (per-user DEVMODE).</summary>
    public static byte[]? GetDevModeLevel(PrinterHandle h, uint level)
    {
        IntPtr buffer = Fill((b, cb) => (GetPrinter(h.Handle, level, b, cb, out var n), n), $"Lecture des réglages (niveau {level}) de « {h.Name} »");
        if (buffer == IntPtr.Zero) return null;
        try
        {
            var p = Marshal.PtrToStructure<PRINTER_INFO_PTR>(buffer);
            return DevMode.CopyFromPointer(p.Pointer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>Writes PRINTER_INFO_8 (global) or PRINTER_INFO_9 (per-user) DEVMODE.</summary>
    public static void SetDevModeLevel(PrinterHandle h, uint level, byte[] devMode)
    {
        IntPtr dm = Marshal.AllocHGlobal(devMode.Length);
        IntPtr info = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            Marshal.Copy(devMode, 0, dm, devMode.Length);
            Marshal.StructureToPtr(new PRINTER_INFO_PTR { Pointer = dm }, info, false);
            if (!SetPrinter(h.Handle, level, info, 0))
            {
                int err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err, $"Écriture des réglages (niveau {level}) : {new Win32Exception(err).Message}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(info);
            Marshal.FreeHGlobal(dm);
        }
    }

    /// <summary>Reads the security descriptor (PRINTER_INFO_3) as a self-relative binary form.</summary>
    public static byte[]? GetSecurityDescriptor(PrinterHandle h)
    {
        IntPtr buffer = Fill((b, cb) => (GetPrinter(h.Handle, 3, b, cb, out var n), n), $"Lecture des permissions de « {h.Name} »");
        if (buffer == IntPtr.Zero) return null;
        try
        {
            var p = Marshal.PtrToStructure<PRINTER_INFO_PTR>(buffer);
            if (p.Pointer == IntPtr.Zero) return null;
            uint len = GetSecurityDescriptorLength(p.Pointer);
            var bytes = new byte[len];
            Marshal.Copy(p.Pointer, bytes, 0, (int)len);
            return bytes;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public static void SetSecurityDescriptor(PrinterHandle h, byte[] selfRelativeSd)
    {
        IntPtr sd = Marshal.AllocHGlobal(selfRelativeSd.Length);
        IntPtr info = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            Marshal.Copy(selfRelativeSd, 0, sd, selfRelativeSd.Length);
            Marshal.StructureToPtr(new PRINTER_INFO_PTR { Pointer = sd }, info, false);
            if (!SetPrinter(h.Handle, 3, info, 0))
            {
                int err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err, $"Écriture des permissions : {new Win32Exception(err).Message}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(info);
            Marshal.FreeHGlobal(sd);
        }
    }

    /// <summary>Asks the driver to validate/merge a DEVMODE (DocumentProperties DM_IN_BUFFER | DM_OUT_BUFFER).</summary>
    public static byte[] ValidateDevMode(PrinterHandle h, string printerName, byte[]? input)
    {
        int size = DocumentProperties(IntPtr.Zero, h.Handle, printerName, IntPtr.Zero, IntPtr.Zero, 0);
        if (size <= 0)
        {
            int err = Marshal.GetLastWin32Error();
            throw new Win32Exception(err, $"Le pilote ne fournit pas de réglages pour « {printerName} » : {new Win32Exception(err).Message}");
        }
        // The input may be larger than what the driver announces (other driver version): give room for both.
        int outSize = Math.Max(size, input?.Length ?? 0) + 4096;
        IntPtr outBuf = Marshal.AllocHGlobal(outSize);
        IntPtr inBuf = IntPtr.Zero;
        try
        {
            int mode = DM_OUT_BUFFER;
            if (input != null)
            {
                inBuf = Marshal.AllocHGlobal(input.Length);
                Marshal.Copy(input, 0, inBuf, input.Length);
                mode |= DM_IN_BUFFER;
            }
            int r = DocumentProperties(IntPtr.Zero, h.Handle, printerName, outBuf, inBuf, mode);
            if (r != IDOK)
            {
                int err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err, $"Le pilote a refusé les réglages de « {printerName} » : {new Win32Exception(err).Message}");
            }
            return DevMode.CopyFromPointer(outBuf) ?? throw new InvalidOperationException("DEVMODE vide.");
        }
        finally
        {
            Marshal.FreeHGlobal(outBuf);
            if (inBuf != IntPtr.Zero) Marshal.FreeHGlobal(inBuf);
        }
    }

    /// <summary>Paper source (bin) id → name pairs, as reported by the driver.</summary>
    public static List<(short id, string name)> GetBins(string printerName, string? portName)
    {
        var result = new List<(short, string)>();
        int count = DeviceCapabilities(printerName, portName, DC_BINS, IntPtr.Zero, IntPtr.Zero);
        int countNames = DeviceCapabilities(printerName, portName, DC_BINNAMES, IntPtr.Zero, IntPtr.Zero);
        if (count <= 0 || countNames != count) return result;
        IntPtr ids = Marshal.AllocHGlobal(count * 2);
        IntPtr names = Marshal.AllocHGlobal(count * 24 * 2);
        try
        {
            if (DeviceCapabilities(printerName, portName, DC_BINS, ids, IntPtr.Zero) <= 0) return result;
            if (DeviceCapabilities(printerName, portName, DC_BINNAMES, names, IntPtr.Zero) <= 0) return result;
            for (int i = 0; i < count; i++)
            {
                short id = Marshal.ReadInt16(ids, i * 2);
                string name = Marshal.PtrToStringUni(names + i * 48, 24).TrimEnd('\0');
                int nul = name.IndexOf('\0');
                if (nul >= 0) name = name[..nul];
                result.Add((id, name));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ids);
            Marshal.FreeHGlobal(names);
        }
        return result;
    }

    public static string? GetDefaultPrinterName()
    {
        int size = 0;
        GetDefaultPrinter(null, ref size);
        if (size <= 0) return null;
        var sb = new StringBuilder(size);
        return GetDefaultPrinter(sb, ref size) ? sb.ToString() : null;
    }

    /// <summary>Recursively reads all values under a printer data key (e.g. "PrinterDriverData").</summary>
    public static List<(string key, string name, uint type, byte[] data)> ReadPrinterData(PrinterHandle h, string rootKey)
    {
        var result = new List<(string, string, uint, byte[])>();
        ReadKey(h, rootKey, result, 0);
        return result;
    }

    private static void ReadKey(PrinterHandle h, string key, List<(string, string, uint, byte[])> result, int depth)
    {
        if (depth > 8) return;
        int rc = EnumPrinterDataEx(h.Handle, key, IntPtr.Zero, 0, out uint needed, out uint count);
        if (rc == ERROR_MORE_DATA && needed > 0)
        {
            IntPtr buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                rc = EnumPrinterDataEx(h.Handle, key, buffer, needed, out needed, out count);
                if (rc != 0) throw new Win32Exception(rc, $"Lecture des données du pilote ({key}) : {new Win32Exception(rc).Message}");
                int size = Marshal.SizeOf<PRINTER_ENUM_VALUES>();
                for (int i = 0; i < count; i++)
                {
                    var v = Marshal.PtrToStructure<PRINTER_ENUM_VALUES>(buffer + i * size);
                    string name = Marshal.PtrToStringUni(v.pValueName) ?? "";
                    var data = new byte[v.cbData];
                    if (v.cbData > 0) Marshal.Copy(v.pData, data, 0, (int)v.cbData);
                    result.Add((key, name, v.dwType, data));
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        else if (rc != 0 && rc != ERROR_FILE_NOT_FOUND)
        {
            throw new Win32Exception(rc, $"Lecture des données du pilote ({key}) : {new Win32Exception(rc).Message}");
        }

        rc = EnumPrinterKey(h.Handle, key, IntPtr.Zero, 0, out uint cbSub);
        if (rc == ERROR_MORE_DATA && cbSub > 0)
        {
            IntPtr sub = Marshal.AllocHGlobal((int)cbSub);
            try
            {
                if (EnumPrinterKey(h.Handle, key, sub, cbSub, out cbSub) == 0)
                {
                    foreach (var subKey in MultiSz(sub, (int)cbSub))
                        ReadKey(h, key + "\\" + subKey, result, depth + 1);
                }
            }
            finally { Marshal.FreeHGlobal(sub); }
        }
    }

    public static List<string> MultiSz(IntPtr p, int byteLength)
    {
        var list = new List<string>();
        if (p == IntPtr.Zero) return list;
        string all = Marshal.PtrToStringUni(p, byteLength / 2);
        foreach (var s in all.Split('\0'))
            if (s.Length > 0) list.Add(s);
        return list;
    }

    public static string? WindowsErrorText(int code) => code == 0 ? null : new Win32Exception(code).Message;

    public static string HResultText(int hr)
    {
        // HRESULT_FROM_WIN32 → Win32 code when possible, for a localized message.
        int code = (hr & 0xFFFF0000) == unchecked((int)0x80070000) ? hr & 0xFFFF : hr;
        return $"{new Win32Exception(code).Message} (0x{hr:X8})";
    }
}
