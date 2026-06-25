using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using BlobInventoryDotNet.Helpers;
using BlobInventoryDotNet.Models;
using BlobInventoryDotNet.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;

namespace BlobInventoryDotNet.Functions;

public class StorageInventoryOrchestrator
{
    private readonly IInventoryService _inventoryService;
    private readonly TokenCredential _credential;
    private readonly ILogger<StorageInventoryOrchestrator> _logger;

    private static string WarehouseAccount =>
        Environment.GetEnvironmentVariable("WAREHOUSE_ACCOUNT") ?? "testconsumption92e8";

    private static string ReportContainer =>
        Environment.GetEnvironmentVariable("REPORT_CONTAINER") ?? "inventory-reports";

    public StorageInventoryOrchestrator(
        IInventoryService inventoryService,
        TokenCredential credential,
        ILogger<StorageInventoryOrchestrator> logger)
    {
        _inventoryService = inventoryService;
        _credential = credential;
        _logger = logger;
    }

    [Function("StorageInventoryHttpStart")]
    public async Task<HttpResponseData> HttpStart(
        [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = "run-inventory-orchestrator")] HttpRequestData req,
        [DurableClient] DurableTaskClient client,
        FunctionContext executionContext)
    {
        ILogger logger = executionContext.GetLogger("RunInventory");

        // Read optional subscriptionId from request body
        string? subscriptionId = null;
        try
        {
            var body = await req.ReadAsStringAsync();
            if (!string.IsNullOrWhiteSpace(body))
            {
                var json = JsonDocument.Parse(body);
                if (json.RootElement.TryGetProperty("subscriptionId", out var subProp))
                    subscriptionId = subProp.GetString();
            }
        }
        catch { /* No body or invalid JSON — proceed without filter */ }

        string instanceId = await client.ScheduleNewOrchestrationInstanceAsync(
            "StorageInventoryOrchestration", subscriptionId);

        logger.LogInformation("Started orchestration with ID = '{instanceId}' for subscription '{sub}'.",
            instanceId, subscriptionId ?? "(all)");

        return client.CreateCheckStatusResponse(req, instanceId);
    }

    [Function("StorageInventoryOrchestration")]
    public async Task<string> RunOrchestrator(
        [OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var logger = context.CreateReplaySafeLogger("StorageInventoryOrchestration");
        var subscriptionId = context.GetInput<string>();
        logger.LogInformation("Starting discovery for subscription: {Sub}", subscriptionId ?? "(all)");

        var accounts = await context.CallActivityAsync<List<StorageAccountQueueMessage>>("DiscoverAccountsActivity", subscriptionId);
        
        logger.LogInformation($"Discovered {accounts.Count} accounts. Starting parallel scans...");
        
        var scanTasks = new List<Task<string>>();
        foreach (var account in accounts)
        {
            scanTasks.Add(context.CallActivityAsync<string>("ScanAccountActivity", account));
        }

        var jsonFiles = await Task.WhenAll(scanTasks);
        var validFiles = jsonFiles.Where(f => !string.IsNullOrEmpty(f)).ToList();

        logger.LogInformation($"Scans completed. Aggregating {validFiles.Count} temporary files...");

        await context.CallActivityAsync("GenerateConsolidatedReportsActivity", validFiles);

        logger.LogInformation("Consolidated reports generated successfully.");
        return "Success";
    }

    [Function("DiscoverAccountsActivity")]
    public async Task<List<StorageAccountQueueMessage>> DiscoverAccounts([ActivityTrigger] string? subscriptionId)
    {
        return await _inventoryService.DiscoverStorageAccountsAsync(subscriptionId);
    }

    [Function("ScanAccountActivity")]
    public async Task<string> ScanAccount([ActivityTrigger] StorageAccountQueueMessage account)
    {
        _logger.LogInformation("Scanning account: {Name}", account.Name);

        var runStart = DateTimeOffset.UtcNow;
        var containerClient = await EnsureContainerExistsAsync(default);
        // Scan all blobs for this specific account to generate a full report
        var records = await _inventoryService.ScanAccountAsync(account, null, default);
        _logger.LogInformation("Discovered {Count} total blobs in account {Name}.", records.Count, account.Name);

        string tempFileName = string.Empty;

        if (records.Count > 0)
        {
            // Save to temp JSON
            var tempContainerClient = await EnsureContainerExistsAsync(default);
            tempFileName = $"temp_{account.Name}_{Guid.NewGuid()}.json";
            var tempBlobClient = tempContainerClient.GetBlobClient(tempFileName);
            
            var jsonContent = JsonSerializer.Serialize(records);
            await tempBlobClient.UploadAsync(BinaryData.FromString(jsonContent), overwrite: true);
            _logger.LogInformation("Uploaded intermediate data to {TempFileName}", tempFileName);
        }
        else
        {
            _logger.LogInformation("No new blobs found for {Name}. Skipping.", account.Name);
        }



        return tempFileName;
    }

    [Function("GenerateConsolidatedReportsActivity")]
    public async Task GenerateConsolidatedReports([ActivityTrigger] List<string> tempFiles)
    {
        var runStart = DateTimeOffset.UtcNow;
        var allRecords = new List<BlobInventoryRecord>();
        var tempContainerClient = await EnsureContainerExistsAsync(default);

        _logger.LogInformation("Downloading and merging {Count} temporary files...", tempFiles.Count);

        foreach (var file in tempFiles)
        {
            var tempBlobClient = tempContainerClient.GetBlobClient(file);
            if (await tempBlobClient.ExistsAsync())
            {
                var response = await tempBlobClient.DownloadContentAsync();
                var content = response.Value.Content.ToString();
                var records = JsonSerializer.Deserialize<List<BlobInventoryRecord>>(content);
                if (records != null)
                {
                    allRecords.AddRange(records);
                }
                // Cleanup temp file
                await tempBlobClient.DeleteIfExistsAsync();
            }
        }

        _logger.LogInformation("Total merged records: {Count}", allRecords.Count);

        // We no longer exit early if allRecords.Count == 0.
        // We want to always generate an empty report and create the container,
        // so the user has proof that the orchestration ran successfully.
        _logger.LogInformation("Consolidating {Count} records.", allRecords.Count);

        var containerClient = await EnsureContainerExistsAsync(default);
        var datePath = runStart.ToString("yyyy/MM/dd");

        // Generate and upload Excel report
        var excelBytes = ExcelHelper.GenerateExcel(allRecords, runStart);
        var excelName = $"{datePath}/blob_inventory_consolidated_{runStart:yyyyMMdd_HHmmss}.xlsx";
        var excelClient = containerClient.GetBlobClient(excelName);
        using (var stream = new MemoryStream(excelBytes, writable: false))
        {
            await excelClient.UploadAsync(stream, overwrite: true);
        }
        _logger.LogInformation("Uploaded consolidated Excel report to {BlobName}", excelName);

        // Generate and upload PDF summary report
        var pdfBytes = PdfHelper.GenerateSummaryReport("Consolidated Inventory", allRecords, runStart);
        var pdfName = $"{datePath}/blob_inventory_summary_consolidated_{runStart:yyyyMMdd_HHmmss}.pdf";
        var pdfClient = containerClient.GetBlobClient(pdfName);
        using (var pdfStream = new MemoryStream(pdfBytes, writable: false))
        {
            await pdfClient.UploadAsync(pdfStream, overwrite: true);
        }
        _logger.LogInformation("Uploaded consolidated PDF report to {BlobName}", pdfName);
    }

    private async Task<BlobContainerClient> EnsureContainerExistsAsync(CancellationToken cancellationToken)
    {
        var serviceUri = new Uri($"https://{WarehouseAccount}.blob.core.windows.net");
        var serviceClient = new BlobServiceClient(serviceUri, _credential);
        var containerClient = serviceClient.GetBlobContainerClient(ReportContainer);
        try
        {
            await containerClient.CreateIfNotExistsAsync(publicAccessType: PublicAccessType.None, cancellationToken: cancellationToken);
        }
        catch (Exception) { /* Ignored */ }
        return containerClient;
    }

    private async Task<BlobContainerClient> EnsureTempContainerExistsAsync(CancellationToken cancellationToken)
    {
        return await EnsureContainerExistsAsync(cancellationToken);
    }
}
