using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Core;
using BlobInventoryDotNet.Helpers;
using BlobInventoryDotNet.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;
using Azure.ResourceManager;
using System.Collections.Generic;

namespace BlobInventoryDotNet.Functions;

/// <summary>
/// Azure Function with two triggers:
/// 1. TimerTrigger — runs daily at 02:00 UTC
/// 2. HttpTrigger  — on-demand execution and testing
///
/// Both triggers execute the same core inventory logic.
/// ONLY an Excel (.xlsx) report is uploaded — no other file types.
/// </summary>
public sealed class BlobInventoryFunction
{
    private readonly IInventoryService _inventoryService;
    private readonly TokenCredential _credential;
    private readonly ILogger<BlobInventoryFunction> _logger;

    // Report destination — read from App Settings
    private static string WarehouseAccount =>
        Environment.GetEnvironmentVariable("WAREHOUSE_ACCOUNT") ?? "testconsumption92e8";

    private static string ReportContainer =>
        Environment.GetEnvironmentVariable("REPORT_CONTAINER") ?? "inventory-reports";

    public BlobInventoryFunction(
        IInventoryService inventoryService,
        TokenCredential credential,
        ILogger<BlobInventoryFunction> logger)
    {
        _inventoryService = inventoryService;
        _credential = credential;
        _logger = logger;
    }

    // ── 1. Timer Trigger ─────────────────────────────────────────────────────
    /// <summary>
    /// Scheduled inventory run — fires daily at 02:00 UTC.
    /// NCRONTAB: second minute hour day month day-of-week
    /// </summary>
    [Function("BlobInventoryTimerTrigger")]
    public async Task RunOnSchedule(
        [TimerTrigger("0 0 2 * * *")] TimerInfo timerInfo,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "BlobInventoryTimerTrigger fired at {Time} UTC. " +
            "IsPastDue: {IsPastDue}. NextOccurrence: {Next}",
            DateTimeOffset.UtcNow,
            timerInfo.IsPastDue,
            timerInfo.ScheduleStatus?.Next);

        await RunInventoryAsync(null, cancellationToken);
    }

    // ── 2. HTTP Trigger ───────────────────────────────────────────────────────
    /// <summary>
    /// On-demand inventory trigger for testing or manual execution.
    /// Route: POST /api/run-inventory
    /// Auth:  Function key required
    /// </summary>
    [Function("BlobInventoryHttpTrigger")]
    public async Task<HttpResponseData> RunOnDemand(
        [HttpTrigger(AuthorizationLevel.Function, "post", "get", Route = "start-inventory")]
        HttpRequestData req,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "BlobInventoryHttpTrigger invoked via {Method} from {Url}",
            req.Method, req.Url);

        var runStart = DateTimeOffset.UtcNow;
        string? subscriptionId = null;

        if (req.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            var requestBody = await new StreamReader(req.Body).ReadToEndAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(requestBody))
            {
                try
                {
                    using var document = JsonDocument.Parse(requestBody);
                    if (document.RootElement.TryGetProperty("subscriptionId", out var subProp))
                    {
                        subscriptionId = subProp.GetString();
                    }
                }
                catch (JsonException)
                {
                    // Ignore parsing error, will just fetch all
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(subscriptionId))
        {
            try
            {
                var armClient = new ArmClient(_credential);
                var resourceId = new ResourceIdentifier($"/subscriptions/{subscriptionId}");
                var subResource = armClient.GetSubscriptionResource(resourceId);
                await subResource.GetAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Validation failed for subscription ID: {SubId}", subscriptionId);
                var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badResponse.WriteStringAsync($"Subscription ID '{subscriptionId}' is invalid or inaccessible. Details: {ex.Message}", cancellationToken);
                return badResponse;
            }
        }

        try
        {
            var result = await RunInventoryAsync(subscriptionId, cancellationToken);

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json; charset=utf-8");

            var payload = new
            {
                status = "success",
                runId = result.RunId,
                blobsDiscovered = result.BlobCount,
                reportBlobPath = result.ReportBlobPath,
                durationSeconds = (DateTimeOffset.UtcNow - runStart).TotalSeconds,
                generatedAt = runStart.ToString("O"),
            };

            await response.WriteStringAsync(
                JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);

            return response;
        }
        catch (OperationCanceledException)
        {
            var cancelResponse = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            await cancelResponse.WriteStringAsync(
                JsonSerializer.Serialize(new { status = "cancelled", message = "Request was cancelled." }),
                cancellationToken: default);
            return cancelResponse;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error in BlobInventoryHttpTrigger");

            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            errorResponse.Headers.Add("Content-Type", "application/json; charset=utf-8");
            await errorResponse.WriteStringAsync(
                JsonSerializer.Serialize(new
                {
                    status = "error",
                    message = "Internal error. See Application Insights logs for details.",
                }),
                cancellationToken: default);
            return errorResponse;
        }
    }

    // ── 3. Get Subscriptions Trigger ──────────────────────────────────────────
    /// <summary>
    /// Gets and validates subscription IDs passed in the POST body for the Logic App.
    /// Route: GET or POST /api/get-subscriptions
    /// Auth:  Function key required
    /// </summary>
    [Function("GetSubscriptionsHttpTrigger")]
    public async Task<HttpResponseData> GetSubscriptions(
        [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = "get-subscriptions")]
        HttpRequestData req,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("GetSubscriptionsHttpTrigger invoked via {Method}", req.Method);
        
        if (req.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var getArmClient = new ArmClient(_credential);
                var subscriptionIds = new List<string>();
                await foreach (var sub in getArmClient.GetSubscriptions().GetAllAsync(cancellationToken))
                {
                    if (!string.IsNullOrWhiteSpace(sub.Data.SubscriptionId))
                    {
                        subscriptionIds.Add(sub.Data.SubscriptionId);
                    }
                }
                var getResponse = req.CreateResponse(HttpStatusCode.OK);
                getResponse.Headers.Add("Content-Type", "application/json; charset=utf-8");
                await getResponse.WriteStringAsync(JsonSerializer.Serialize(subscriptionIds), cancellationToken);
                return getResponse;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching subscriptions");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteStringAsync($"Error listing subscriptions: {ex.Message}", cancellationToken);
                return errorResponse;
            }
        }

        var rawSubIds = new List<string>();

        // Read and parse request body
        var requestBody = await new StreamReader(req.Body).ReadToEndAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(requestBody))
        {
            var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badResponse.WriteStringAsync("Request body cannot be empty. Please provide a JSON array of subscription IDs.", cancellationToken);
            return badResponse;
        }

        try
        {
            using var document = JsonDocument.Parse(requestBody);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var elem in document.RootElement.EnumerateArray())
                {
                    var val = elem.GetString();
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        rawSubIds.Add(val);
                    }
                }
            }
        }
        catch (JsonException)
        {
            var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badResponse.WriteStringAsync("Invalid JSON format. Please provide a JSON array of subscription IDs.", cancellationToken);
            return badResponse;
        }

        if (rawSubIds.Count == 0)
        {
            var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badResponse.WriteStringAsync("No subscription IDs were provided in the request body.", cancellationToken);
            return badResponse;
        }

        // Validate each subscription ID via ARM Client
        var armClient = new ArmClient(_credential);
        var validatedSubIds = new List<string>();

        foreach (var subId in rawSubIds)
        {
            try
            {
                var resourceId = new ResourceIdentifier($"/subscriptions/{subId}");
                var subResource = armClient.GetSubscriptionResource(resourceId);
                // Fetch to verify active access/existence
                await subResource.GetAsync(cancellationToken);
                validatedSubIds.Add(subId);
            }
            catch (Azure.RequestFailedException ex)
            {
                _logger.LogError(ex, "Validation failed for subscription: {SubId}", subId);
                var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badResponse.WriteStringAsync($"Subscription ID '{subId}' is invalid or inaccessible (HTTP {ex.Status}). Details: {ex.Message}", cancellationToken);
                return badResponse;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error validating subscription: {SubId}", subId);
                var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badResponse.WriteStringAsync($"Error validating Subscription ID '{subId}': {ex.Message}", cancellationToken);
                return badResponse;
            }
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(validatedSubIds), cancellationToken);
        return response;
    }

    // ── Core Logic ────────────────────────────────────────────────────────────
    /// <summary>
    /// Executes the full inventory pipeline:
    /// 1. Collect inventory from all subscriptions (or the provided one)
    /// 2. Generate Excel and PDF reports in memory
    /// 3. Upload both reports to inventory-reports container
    /// </summary>
    private async Task<InventoryRunResult> RunInventoryAsync(string? subscriptionId, CancellationToken cancellationToken)
    {
        var runStart = DateTimeOffset.UtcNow;
        var runId = $"inventory_{runStart:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}"[..36];

        _logger.LogInformation("=== Inventory Run Started: {RunId} ===", runId);

        // Calculate total subscriptions scanned
        int totalSubs = 0;
        if (!string.IsNullOrWhiteSpace(subscriptionId))
        {
            totalSubs = 1;
        }
        else
        {
            var armClient = new ArmClient(_credential);
            await foreach (var sub in armClient.GetSubscriptions().GetAllAsync(cancellationToken))
            {
                totalSubs++;
            }
        }

        // ── Step 1: Collect inventory ─────────────────────────────────────────
        _logger.LogInformation("Step 1/3: Collecting blob inventory...");
        var records = await _inventoryService.CollectInventoryAsync(subscriptionId, cancellationToken);

        _logger.LogInformation("Step 1/3 complete: {Count} blobs discovered.", records.Count);

        // ── Step 2: Generate reports in memory ───────────────────────────
        _logger.LogInformation("Step 2/3: Generating reports...");
        var excelBytes = ExcelHelper.GenerateExcel(records, runStart);
        _logger.LogInformation("Excel report generated ({Bytes:N0} bytes).", excelBytes.Length);

        var pdfBytes = PdfHelper.GeneratePdfSummary(records, totalSubs, runStart);
        _logger.LogInformation("PDF summary report generated ({Bytes:N0} bytes).", pdfBytes.Length);

        // ── Step 3: Upload files to inventory-reports ─────────────────
        _logger.LogInformation("Step 3/3: Uploading reports to {Account}/{Container}...",
            WarehouseAccount, ReportContainer);

        // Partition by date for easy browsing in the portal
        var datePath = runStart.ToString("yyyy/MM/dd");
        var excelBlobName = $"{datePath}/blob_inventory_{runStart:yyyyMMdd_HHmmss}.xlsx";
        var pdfBlobName = $"{datePath}/blob_inventory_summary_{runStart:yyyyMMdd_HHmmss}.pdf";

        var containerClient = await EnsureContainerExistsAsync(cancellationToken);

        // Upload Excel
        var excelBlobClient = containerClient.GetBlobClient(excelBlobName);
        using (var excelStream = new MemoryStream(excelBytes, writable: false))
        {
            await excelBlobClient.UploadAsync(excelStream, overwrite: true, cancellationToken: cancellationToken);
        }
        _logger.LogInformation("Excel report uploaded → {BlobName} ({Bytes:N0} bytes).", excelBlobName, excelBytes.Length);

        // Upload PDF
        var pdfBlobClient = containerClient.GetBlobClient(pdfBlobName);
        using (var pdfStream = new MemoryStream(pdfBytes, writable: false))
        {
            await pdfBlobClient.UploadAsync(pdfStream, overwrite: true, cancellationToken: cancellationToken);
        }
        _logger.LogInformation("PDF report uploaded → {BlobName} ({Bytes:N0} bytes).", pdfBlobName, pdfBytes.Length);

        _logger.LogInformation(
            "=== Inventory Run Complete: {RunId} | Blobs: {Count} | Duration: {Elapsed:hh\\:mm\\:ss} ===",
            runId, records.Count, DateTimeOffset.UtcNow - runStart);

        return new InventoryRunResult(runId, records.Count, excelBlobName);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PRIVATE: Ensure the report container exists
    // ─────────────────────────────────────────────────────────────────────────
    private async Task<BlobContainerClient> EnsureContainerExistsAsync(
        CancellationToken cancellationToken)
    {
        var serviceUri = new Uri($"https://{WarehouseAccount}.blob.core.windows.net");
        var serviceClient = new BlobServiceClient(serviceUri, _credential);
        var containerClient = serviceClient.GetBlobContainerClient(ReportContainer);

        try
        {
            await containerClient.CreateIfNotExistsAsync(
                publicAccessType: PublicAccessType.None,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not create container '{Container}'. It may already exist.", ReportContainer);
        }

        return containerClient;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PRIVATE: Result record
    // ─────────────────────────────────────────────────────────────────────────
    private sealed record InventoryRunResult(string RunId, int BlobCount, string ReportBlobPath);
}
