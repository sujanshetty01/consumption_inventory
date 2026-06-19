using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BlobInventoryDotNet.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace BlobInventoryDotNet.Helpers;

public static class PdfHelper
{
    static PdfHelper()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public static byte[] GenerateSummaryReport(string accountName, List<BlobInventoryRecord> records, DateTimeOffset scanDate)
    {
        var totalBlobs = records.Count;
        var totalBytes = records.Sum(r => r.BlobSizeBytes);
        var largestBlob = records.OrderByDescending(r => r.BlobSizeBytes).FirstOrDefault();
        var oldestBlob = records.OrderBy(r => r.LastModifiedDate).FirstOrDefault();

        var accountStats = records
            .GroupBy(r => r.StorageAccountName)
            .Select(g => new AccountStat
            {
                Account = g.Key,
                Count = g.Count(),
                TotalBytes = g.Sum(r => r.BlobSizeBytes)
            })
            .OrderByDescending(x => x.TotalBytes)
            .ToList();

        using var memoryStream = new MemoryStream();

        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(2, Unit.Centimetre);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(x => x.FontSize(11).FontFamily(Fonts.Arial));

                page.Header().Element(ComposeHeader);
                page.Content().Element(x => ComposeContent(x, accountName, totalBlobs, totalBytes, largestBlob, oldestBlob, accountStats, scanDate));
                page.Footer().AlignCenter().Text(x =>
                {
                    x.CurrentPageNumber();
                    x.Span(" / ");
                    x.TotalPages();
                });
            });
        })
        .GeneratePdf(memoryStream);

        return memoryStream.ToArray();
    }

    private static void ComposeHeader(IContainer container)
    {
        container.Row(row =>
        {
            row.RelativeItem().Column(column =>
            {
                column.Item().Text("Storage Inventory Summary").FontSize(20).SemiBold().FontColor(Colors.Blue.Darken2);
                column.Item().Text($"Generated on {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC").FontSize(10).FontColor(Colors.Grey.Medium);
            });
        });
    }

    private static void ComposeContent(
        IContainer container, 
        string accountName, 
        int totalBlobs, 
        long totalBytes, 
        BlobInventoryRecord? largestBlob, 
        BlobInventoryRecord? oldestBlob, 
        IEnumerable<AccountStat> accountStats, 
        DateTimeOffset scanDate)
    {
        container.PaddingVertical(1, Unit.Centimetre).Column(column =>
        {
            column.Spacing(15);

            column.Item().Text($"Report Scope: {accountName}").FontSize(14).SemiBold();
            column.Item().Text($"Scan Date: {scanDate:yyyy-MM-dd HH:mm:ss} UTC").FontSize(12);

            column.Item().LineHorizontal(1).LineColor(Colors.Grey.Lighten2);

            // Summary Stats
            column.Item().Text("Executive Summary").FontSize(14).SemiBold().FontColor(Colors.Blue.Darken2);
            column.Item().Text($"Total Incremental Blobs Discovered: {totalBlobs:N0}");
            column.Item().Text($"Total Incremental Size: {FormatBytes(totalBytes)}");

            if (largestBlob != null)
            {
                column.Item().Text($"Largest Blob: {largestBlob.BlobName} ({FormatBytes(largestBlob.BlobSizeBytes)}) in [{largestBlob.ContainerName}]");
            }
            if (oldestBlob != null)
            {
                column.Item().Text($"Oldest Blob: {oldestBlob.BlobName} (Modified: {oldestBlob.LastModifiedDate:yyyy-MM-dd})");
            }

            column.Item().LineHorizontal(1).LineColor(Colors.Grey.Lighten2);

            // Account Breakdown
            column.Item().Text("Usage Breakdown by Storage Account").FontSize(14).SemiBold().FontColor(Colors.Blue.Darken2);

            column.Item().Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(3);
                    columns.RelativeColumn(1);
                    columns.RelativeColumn(2);
                });

                table.Header(header =>
                {
                    header.Cell().BorderBottom(1).PaddingBottom(5).Text("Storage Account").SemiBold();
                    header.Cell().BorderBottom(1).PaddingBottom(5).AlignRight().Text("Blob Count").SemiBold();
                    header.Cell().BorderBottom(1).PaddingBottom(5).AlignRight().Text("Total Size").SemiBold();
                });

                foreach (var stat in accountStats)
                {
                    table.Cell().PaddingVertical(2).Text(stat.Account);
                    table.Cell().PaddingVertical(2).AlignRight().Text($"{stat.Count:N0}");
                    table.Cell().PaddingVertical(2).AlignRight().Text(FormatBytes(stat.TotalBytes));
                }
            });

            column.Item().LineHorizontal(1).LineColor(Colors.Grey.Lighten2);

            // Recommendations
            column.Item().Text("Recommendations").FontSize(14).SemiBold().FontColor(Colors.Blue.Darken2);
            column.Item().Text("• Review large blobs for potential archival to Cool/Archive tiers.");
            column.Item().Text("• Consider setting up Azure Blob Lifecycle Management policies for older blobs.");
            column.Item().Text("• Validate if development containers (if any) can be cleaned up periodically.");
        });
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        int order = 0;
        double len = bytes;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}

public class AccountStat
{
    public string Account { get; set; } = string.Empty;
    public int Count { get; set; }
    public long TotalBytes { get; set; }
}
