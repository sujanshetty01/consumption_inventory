using BlobInventoryDotNet.Models;
using ClosedXML.Excel;

namespace BlobInventoryDotNet.Helpers;

/// <summary>
/// Generates Excel (.xlsx) inventory reports using ClosedXML.
/// Produces a single workbook with one "Blob Inventory" sheet.
/// No disk I/O — returns bytes directly.
/// </summary>
public static class ExcelHelper
{
    // Column headers exactly as specified in requirements
    private static readonly string[] Headers = new[]
    {
        "Subscription ID",
        "Resource Group Name",
        "Storage Account Name",
        "Container Name",
        "Blob Name",
        "Blob Size (Bytes)",
        "Access Tier",
        "Creation Date (UTC)",
        "Last Modified Date (UTC)"
    };

    /// <summary>
    /// Generates an Excel workbook from the inventory records.
    /// </summary>
    /// <param name="records">The blob inventory records to include.</param>
    /// <param name="runTimestamp">The timestamp of this inventory run (for the sheet title).</param>
    /// <returns>Raw bytes of the .xlsx file.</returns>
    public static byte[] GenerateExcel(
        IReadOnlyList<BlobInventoryRecord> records,
        DateTimeOffset runTimestamp)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("Blob Inventory");

        // ── Header row ───────────────────────────────────────────────────────
        for (int col = 1; col <= Headers.Length; col++)
        {
            var cell = ws.Cell(1, col);
            cell.Value = Headers[col - 1];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Fill.BackgroundColor = XLColor.FromArgb(0x21, 0x56, 0x32); // Azure dark green
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        }

        // Freeze header row
        ws.SheetView.FreezeRows(1);

        // ── Data rows ─────────────────────────────────────────────────────────
        int row = 2;
        foreach (var rec in records)
        {
            ws.Cell(row, 1).Value = rec.SubscriptionId;
            ws.Cell(row, 2).Value = rec.ResourceGroupName;
            ws.Cell(row, 3).Value = rec.StorageAccountName;
            ws.Cell(row, 4).Value = rec.ContainerName;
            ws.Cell(row, 5).Value = rec.BlobName;
            ws.Cell(row, 6).Value = rec.BlobSizeBytes;
            ws.Cell(row, 7).Value = rec.AccessTier ?? "Unknown";

            // Date columns — stored as text ISO 8601 for universal readability
            ws.Cell(row, 8).Value = rec.CreationDate.HasValue
                ? rec.CreationDate.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss")
                : "";
            ws.Cell(row, 9).Value = rec.LastModifiedDate.HasValue
                ? rec.LastModifiedDate.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss")
                : "";

            // Alternate row shading for readability
            if (row % 2 == 0)
            {
                ws.Row(row).Style.Fill.BackgroundColor = XLColor.FromArgb(0xF2, 0xF2, 0xF2);
            }

            row++;
        }

        // ── Formatting ────────────────────────────────────────────────────────
        // Auto-fit all columns
        ws.Columns().AdjustToContents();

        // Cap blob name column width to avoid extreme widths
        if (ws.Column(5).Width > 80) ws.Column(5).Width = 80;

        // Add auto-filter on header row
        var dataRange = ws.Range(ws.Cell(1, 1), ws.Cell(Math.Max(row - 1, 1), Headers.Length));
        dataRange.SetAutoFilter();

        // Add metadata in a separate tab
        var metaWs = workbook.Worksheets.Add("Report Info");
        metaWs.Cell(1, 1).Value = "Generated At (UTC)";
        metaWs.Cell(1, 2).Value = runTimestamp.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        metaWs.Cell(2, 1).Value = "Total Blobs";
        metaWs.Cell(2, 2).Value = records.Count;
        metaWs.Cell(3, 1).Value = "Total Size (Bytes)";
        metaWs.Cell(3, 2).Value = records.Sum(r => r.BlobSizeBytes);
        metaWs.Cell(4, 1).Value = "Total Size (GB)";
        metaWs.Cell(4, 2).Value = Math.Round(records.Sum(r => r.BlobSizeBytes) / (1024.0 * 1024 * 1024), 4);
        metaWs.Cell(5, 1).Value = "Unique Subscriptions";
        metaWs.Cell(5, 2).Value = records.Select(r => r.SubscriptionId).Distinct().Count();
        metaWs.Cell(6, 1).Value = "Unique Storage Accounts";
        metaWs.Cell(6, 2).Value = records.Select(r => r.StorageAccountName).Distinct().Count();

        metaWs.Column(1).Style.Font.Bold = true;
        metaWs.Columns().AdjustToContents();

        // ── Serialize to bytes (zero disk I/O) ────────────────────────────────
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }
}
