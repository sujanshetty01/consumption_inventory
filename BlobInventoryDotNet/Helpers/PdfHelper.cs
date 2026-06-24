using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BlobInventoryDotNet.Models;
using PdfSharpCore.Pdf;
using PdfSharpCore.Drawing;

namespace BlobInventoryDotNet.Helpers;

public static class PdfHelper
{
    public static byte[] GeneratePdfSummary(
        IReadOnlyList<BlobInventoryRecord> records,
        int totalSubscriptionsScanned,
        DateTimeOffset runTimestamp)
    {
        // ── 1. Calculate General Metrics ─────────────────────────────────────
        int uniqueSubsWithData = records.Select(r => r.SubscriptionId).Distinct().Count();
        int uniqueStorageAccounts = records.Select(r => r.StorageAccountName).Distinct().Count();
        int totalContainers = records.Select(r => $"{r.StorageAccountName}/{r.ContainerName}").Distinct().Count();
        int totalBlobs = records.Count;
        long totalBytes = records.Sum(r => r.BlobSizeBytes);
        double totalGb = totalBytes / (1024.0 * 1024 * 1024);
        double avgBlobSizeMb = totalBlobs > 0 ? (totalBytes / (1024.0 * 1024)) / totalBlobs : 0.0;

        // Date ranges
        var validDates = records.Where(r => r.LastModifiedDate.HasValue).Select(r => r.LastModifiedDate!.Value).ToList();
        string oldestBlobStr = validDates.Any() ? validDates.Min().UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss") : "N/A";
        string newestBlobStr = validDates.Any() ? validDates.Max().UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss") : "N/A";

        // Group by Storage Account
        var saSummaries = records
            .GroupBy(r => r.StorageAccountName)
            .Select(g => new
            {
                Account = g.Key,
                Subscription = g.First().SubscriptionId,
                ContainersCount = g.Select(r => r.ContainerName).Distinct().Count(),
                BlobsCount = g.Count(),
                Bytes = g.Sum(r => r.BlobSizeBytes)
            })
            .OrderByDescending(g => g.Bytes)
            .ToList();

        // Storage Account with most blobs
        var saMostBlobs = saSummaries.OrderByDescending(s => s.BlobsCount).FirstOrDefault();
        string mostBlobsAccountStr = saMostBlobs != null ? $"{saMostBlobs.Account} ({saMostBlobs.BlobsCount:N0} blobs)" : "N/A";

        // Group by Access Tier
        var tierGroups = records
            .GroupBy(r => r.AccessTier ?? "Unknown")
            .Select(g => new
            {
                Tier = g.Key,
                Count = g.Count(),
                Bytes = g.Sum(r => r.BlobSizeBytes)
            })
            .OrderByDescending(g => g.Bytes)
            .ToList();

        // ── 2. Initialize PDF Document ────────────────────────────────────────
        var document = new PdfDocument();
        document.Info.Title = "Azure Storage Inventory & Analytics Insights";
        
        var page = document.AddPage();
        page.Size = PdfSharpCore.PageSize.A4;
        var gfx = XGraphics.FromPdfPage(page);
        
        // Define Fonts
        var fontTitle = new XFont("Arial", 18, XFontStyle.Bold);
        var fontSubtitle = new XFont("Arial", 9, XFontStyle.Italic);
        var fontSection = new XFont("Arial", 12, XFontStyle.Bold);
        var fontHeader = new XFont("Arial", 9, XFontStyle.Bold);
        var fontBody = new XFont("Arial", 9, XFontStyle.Regular);
        var fontBodyBold = new XFont("Arial", 9, XFontStyle.Bold);
        
        // Define Colors
        var colorPrimary = XColor.FromArgb(24, 43, 73); // Deep Navy Blue
        var colorAccent = XColor.FromArgb(220, 160, 40); // Gold/Orange accent
        XPen borderPen = new XPen(XColors.LightGray, 0.5);
        XSolidBrush headerBrush = new XSolidBrush(XColor.FromArgb(235, 240, 245));
        XSolidBrush textBrush = new XSolidBrush(XColors.Black);
        XSolidBrush whiteBrush = new XSolidBrush(XColors.White);

        double yPos = 40;

        // Helper to check for new page
        Action<double> checkPageOverflow = (neededHeight) =>
        {
            if (yPos + neededHeight > page.Height - 60)
            {
                page = document.AddPage();
                page.Size = PdfSharpCore.PageSize.A4;
                gfx = XGraphics.FromPdfPage(page);
                yPos = 40;
            }
        };

        // ── Header/Banner ─────────────────────────────────────────────────────
        gfx.DrawString("AZURE STORAGE INVENTORY & SUMMARY REPORT", fontTitle, new XSolidBrush(colorPrimary), new XRect(40, yPos, page.Width - 80, 25), XStringFormats.TopLeft);
        yPos += 22;
        
        gfx.DrawString($"Report generated on: {runTimestamp.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC  |  Scope: {totalSubscriptionsScanned} subscription(s)", fontSubtitle, new XSolidBrush(XColors.Gray), new XRect(40, yPos, page.Width - 80, 15), XStringFormats.TopLeft);
        yPos += 15;

        // Colored accent line under header
        gfx.DrawLine(new XPen(colorAccent, 2.5), 40, yPos, page.Width - 40, yPos);
        yPos += 20;

        // ── Section 1: Executive Overview ─────────────────────────────────────
        checkPageOverflow(110);
        gfx.DrawString("1. EXECUTIVE INVENTORY OVERVIEW", fontSection, new XSolidBrush(colorPrimary), new XRect(40, yPos, page.Width - 80, 20), XStringFormats.TopLeft);
        yPos += 20;

        DrawMetadataRow(gfx, fontBodyBold, fontBody, textBrush, borderPen, ref yPos, "Subscriptions Scanned:", $"{totalSubscriptionsScanned}");
        DrawMetadataRow(gfx, fontBodyBold, fontBody, textBrush, borderPen, ref yPos, "Active Subscriptions (with data):", $"{uniqueSubsWithData}");
        DrawMetadataRow(gfx, fontBodyBold, fontBody, textBrush, borderPen, ref yPos, "Total Storage Accounts Scanned:", $"{uniqueStorageAccounts}");
        DrawMetadataRow(gfx, fontBodyBold, fontBody, textBrush, borderPen, ref yPos, "Total Containers Found:", $"{totalContainers}");
        DrawMetadataRow(gfx, fontBodyBold, fontBody, textBrush, borderPen, ref yPos, "Total Blobs Enumerated:", $"{totalBlobs:N0}");
        DrawMetadataRow(gfx, fontBodyBold, fontBody, textBrush, borderPen, ref yPos, "Total Capacity Used:", $"{totalGb:N3} GB ({totalBytes:N0} bytes)");
        yPos += 20;

        // ── Section 2: Storage Accounts Summary Table ─────────────────────────
        checkPageOverflow(70);
        gfx.DrawString("2. STORAGE ACCOUNTS DETAILED SUMMARY", fontSection, new XSolidBrush(colorPrimary), new XRect(40, yPos, page.Width - 80, 20), XStringFormats.TopLeft);
        yPos += 20;

        double saX = 40;
        double saW1 = 130; // Storage Account Name
        double saW2 = 180; // Subscription ID
        double saW3 = 60;  // Containers
        double saW4 = 70;  // Blobs
        double saW5 = 75;  // Size (GB)
        double rowH = 18;

        // Table Header
        gfx.DrawRectangle(headerBrush, saX, yPos, saW1 + saW2 + saW3 + saW4 + saW5, rowH);
        gfx.DrawRectangle(borderPen, saX, yPos, saW1 + saW2 + saW3 + saW4 + saW5, rowH);
        gfx.DrawString("Storage Account", fontHeader, textBrush, new XRect(saX + 4, yPos, saW1 - 8, rowH), XStringFormats.CenterLeft);
        gfx.DrawString("Subscription ID", fontHeader, textBrush, new XRect(saX + saW1 + 4, yPos, saW2 - 8, rowH), XStringFormats.CenterLeft);
        gfx.DrawString("Containers", fontHeader, textBrush, new XRect(saX + saW1 + saW2 + 4, yPos, saW3 - 8, rowH), XStringFormats.CenterLeft);
        gfx.DrawString("Blobs", fontHeader, textBrush, new XRect(saX + saW1 + saW2 + saW3 + 4, yPos, saW4 - 8, rowH), XStringFormats.CenterLeft);
        gfx.DrawString("Size (GB)", fontHeader, textBrush, new XRect(saX + saW1 + saW2 + saW3 + saW4 + 4, yPos, saW5 - 8, rowH), XStringFormats.CenterLeft);
        yPos += rowH;

        if (!saSummaries.Any())
        {
            gfx.DrawRectangle(borderPen, saX, yPos, saW1 + saW2 + saW3 + saW4 + saW5, rowH);
            gfx.DrawString("No storage account inventory data found.", fontBody, textBrush, new XRect(saX + 4, yPos, saW1 + saW2 + saW3 + saW4 + saW5 - 8, rowH), XStringFormats.CenterLeft);
            yPos += rowH;
        }
        else
        {
            foreach (var sa in saSummaries)
            {
                checkPageOverflow(saX);
                gfx.DrawRectangle(borderPen, saX, yPos, saW1 + saW2 + saW3 + saW4 + saW5, rowH);
                gfx.DrawString(sa.Account, fontBody, textBrush, new XRect(saX + 4, yPos, saW1 - 8, rowH), XStringFormats.CenterLeft);
                gfx.DrawString(sa.Subscription, fontBody, textBrush, new XRect(saX + saW1 + 4, yPos, saW2 - 8, rowH), XStringFormats.CenterLeft);
                gfx.DrawString(sa.ContainersCount.ToString("N0"), fontBody, textBrush, new XRect(saX + saW1 + saW2 + 4, yPos, saW3 - 8, rowH), XStringFormats.CenterLeft);
                gfx.DrawString(sa.BlobsCount.ToString("N0"), fontBody, textBrush, new XRect(saX + saW1 + saW2 + saW3 + 4, yPos, saW4 - 8, rowH), XStringFormats.CenterLeft);
                gfx.DrawString((sa.Bytes / (1024.0 * 1024 * 1024)).ToString("N4"), fontBody, textBrush, new XRect(saX + saW1 + saW2 + saW3 + saW4 + 4, yPos, saW5 - 8, rowH), XStringFormats.CenterLeft);
                yPos += rowH;
            }
        }
        yPos += 20;

        // ── Section 3: Access Tier Breakdown ──────────────────────────────────
        checkPageOverflow(70);
        gfx.DrawString("3. ACCESS TIER SUMMARY", fontSection, new XSolidBrush(colorPrimary), new XRect(40, yPos, page.Width - 80, 20), XStringFormats.TopLeft);
        yPos += 20;

        double atX = 40;
        double atW1 = 180; // Tier
        double atW2 = 160; // Count
        double atW3 = 175; // Size (GB)

        gfx.DrawRectangle(headerBrush, atX, yPos, atW1 + atW2 + atW3, rowH);
        gfx.DrawRectangle(borderPen, atX, yPos, atW1 + atW2 + atW3, rowH);
        gfx.DrawString("Access Tier", fontHeader, textBrush, new XRect(atX + 4, yPos, atW1 - 8, rowH), XStringFormats.CenterLeft);
        gfx.DrawString("Blobs Count", fontHeader, textBrush, new XRect(atX + atW1 + 4, yPos, atW2 - 8, rowH), XStringFormats.CenterLeft);
        gfx.DrawString("Total Size (GB)", fontHeader, textBrush, new XRect(atX + atW1 + atW2 + 4, yPos, atW3 - 8, rowH), XStringFormats.CenterLeft);
        yPos += rowH;

        foreach (var tier in tierGroups)
        {
            checkPageOverflow(rowH);
            gfx.DrawRectangle(borderPen, atX, yPos, atW1 + atW2 + atW3, rowH);
            gfx.DrawString(tier.Tier, fontBody, textBrush, new XRect(atX + 4, yPos, atW1 - 8, rowH), XStringFormats.CenterLeft);
            gfx.DrawString(tier.Count.ToString("N0"), fontBody, textBrush, new XRect(atX + atW1 + 4, yPos, atW2 - 8, rowH), XStringFormats.CenterLeft);
            gfx.DrawString((tier.Bytes / (1024.0 * 1024 * 1024)).ToString("N4"), fontBody, textBrush, new XRect(atX + atW1 + atW2 + 4, yPos, atW3 - 8, rowH), XStringFormats.CenterLeft);
            yPos += rowH;
        }
        yPos += 20;

        // ── Section 4: Inventory Insights & Optimization Recommendations ───────
        checkPageOverflow(90);
        gfx.DrawString("4. INVENTORY INSIGHTS & RECOMMENDATIONS", fontSection, new XSolidBrush(colorPrimary), new XRect(40, yPos, page.Width - 80, 20), XStringFormats.TopLeft);
        yPos += 20;

        DrawMetadataRow(gfx, fontBodyBold, fontBody, textBrush, borderPen, ref yPos, "Average Blob Size:", $"{avgBlobSizeMb:N3} MB");
        DrawMetadataRow(gfx, fontBodyBold, fontBody, textBrush, borderPen, ref yPos, "Max Density Storage Account:", mostBlobsAccountStr);
        DrawMetadataRow(gfx, fontBodyBold, fontBody, textBrush, borderPen, ref yPos, "Oldest Modified Blob Date:", oldestBlobStr);
        DrawMetadataRow(gfx, fontBodyBold, fontBody, textBrush, borderPen, ref yPos, "Newest Modified Blob Date:", newestBlobStr);

        // Calculate cost candidates (Hot blobs older than 90 days)
        var costCandidates = records
            .Where(r => (r.AccessTier == null || r.AccessTier.Equals("Hot", StringComparison.OrdinalIgnoreCase)) &&
                         r.LastModifiedDate.HasValue &&
                         r.LastModifiedDate.Value.UtcDateTime < DateTime.UtcNow.AddDays(-90))
            .ToList();

        double candidateGb = costCandidates.Sum(c => c.BlobSizeBytes) / (1024.0 * 1024 * 1024);
        string recommendationStr = candidateGb > 0 
            ? $"Identified {costCandidates.Count:N0} blobs ({candidateGb:N3} GB) older than 90 days in Hot tier. Recommend transitioning to Cool/Archive."
            : "No old Hot blobs found. Storage tiering distribution is well optimized.";

        DrawMetadataRow(gfx, fontBodyBold, fontBody, textBrush, borderPen, ref yPos, "Cost Optimization Tip:", recommendationStr);

        // ── 8. Save Document and Return ───────────────────────────────────────
        using var ms = new MemoryStream();
        document.Save(ms);
        return ms.ToArray();
    }

    private static void DrawMetadataRow(
        XGraphics gfx,
        XFont labelFont,
        XFont valFont,
        XSolidBrush brush,
        XPen borderPen,
        ref double yPos,
        string label,
        string value)
    {
        double x = 40;
        double w1 = 180;
        double w2 = 335;
        double h = 18;

        gfx.DrawRectangle(borderPen, x, yPos, w1 + w2, h);
        gfx.DrawString(label, labelFont, brush, new XRect(x + 5, yPos, w1 - 10, h), XStringFormats.CenterLeft);
        
        // Wrap/truncation logic to prevent long values from clipping
        string printedVal = value;
        if (value.Length > 85)
        {
            printedVal = value.Substring(0, 82) + "...";
        }
        gfx.DrawString(printedVal, valFont, brush, new XRect(x + w1 + 5, yPos, w2 - 10, h), XStringFormats.CenterLeft);

        yPos += h;
    }
}
