using System.Printing;
using System.Printing.Interop;

namespace DataBridge.Printers.Inventory;

/// <summary>DEVMODE ↔ PrintTicket conversion through the driver's PrintTicket provider (System.Printing).</summary>
public static class Tickets
{
    public static byte[] DevModeToTicketXml(string printerName, byte[] devMode)
    {
        using var conv = new PrintTicketConverter(printerName, PrintTicketConverter.MaxPrintSchemaVersion);
        var ticket = conv.ConvertDevModeToPrintTicket(devMode, PrintTicketScope.JobScope);
        using var ms = ticket.GetXmlStream();
        return ms.ToArray();
    }

    /// <summary>Converts a PrintTicket into a DEVMODE of the target printer's driver, starting from its user or queue defaults.</summary>
    public static byte[] TicketXmlToDevMode(string printerName, byte[] ticketXml, bool userBase)
    {
        using var conv = new PrintTicketConverter(printerName, PrintTicketConverter.MaxPrintSchemaVersion);
        var ticket = new PrintTicket(new MemoryStream(ticketXml));
        return conv.ConvertPrintTicketToDevMode(ticket, userBase ? BaseDevModeType.UserDefault : BaseDevModeType.PrinterDefault);
    }
}
