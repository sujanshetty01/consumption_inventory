using System.Net.Http.Headers;
using System.Text;
using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using BlobInventoryDotNet.Helpers;
using BlobInventoryDotNet.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Linq;

namespace BlobInventoryDotNet.Services;

/// <summary>
/// Discovers storage accounts across all accessible Azure subscriptions
/// and enumerates blobs within them. Uses Managed Identity in production.
/// </summary>
public sealed class InventoryService : IInventoryService
{
    // ── Containers to exclude from inventory ────────────────────────────────
    private static readonly string[] ExcludedPrefixes =
    {
        "azure-webjobs-",
        "app-package-",
        "scm-releases",
    };

    private static readonly HashSet<string> ExcludedExact =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "$logs",
            "$blobchangefeed",
            "$web",
            "$root",
            "inventory-reports",   // self-exclusion — don't inventory the report container
        };

    // ARG REST endpoint
    private const string ArgEndpoint = "https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2021-03-01";

    // ARG KQL query
    private const string ArgKql = @"
        Resources
        | where type =~ 'microsoft.storage/storageaccounts'
        | project name, resourceGroup, subscriptionId, location
        | order by subscriptionId asc, name asc";

    private readonly TokenCredential _credential;
    private readonly ILogger<InventoryService> _logger;

    // Shared HttpClient — reuse across invocations
    private static readonly HttpClient HttpClient = new();

    public InventoryService(TokenCredential credential, ILogger<InventoryService> logger)
    {
        _credential = credential ?? throw new ArgumentNullException(nameof(credential));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BlobInventoryRecord>> CollectInventoryAsync(
        string? subscriptionId = default,
        CancellationToken cancellationToken = default)
    {
        var allRecords = new List<BlobInventoryRecord>();
        var startTime = DateTimeOffset.UtcNow;

        // ── Step 1: Enumerate accessible subscriptions ─────────────────────
        var armClient = new ArmClient(_credential);
        var subscriptionIds = new List<string>();

        if (!string.IsNullOrWhiteSpace(subscriptionId))
        {
            subscriptionIds.Add(subscriptionId);
            _logger.LogInformation("Using explicitly provided subscription: {Id}", subscriptionId);
        }
        else
        {
            _logger.LogInformation("Enumerating accessible subscriptions...");
            await foreach (var sub in armClient.GetSubscriptions()
                               .GetAllAsync(cancellationToken: cancellationToken))
            {
                if (!string.IsNullOrWhiteSpace(sub.Data.SubscriptionId))
                {
                    subscriptionIds.Add(sub.Data.SubscriptionId);
                    _logger.LogInformation("  Subscription: {Id} ({Name})",
                        sub.Data.SubscriptionId, sub.Data.DisplayName);
                }
            }
        }

        if (subscriptionIds.Count == 0)
        {
            _logger.LogWarning("No subscriptions found via credential. " +
                               "Verify Managed Identity has Reader role.");
            return allRecords;
        }

        // ── Step 2: Discover storage accounts via ARG ─────────────────────
        _logger.LogInformation("Querying Azure Resource Graph across {N} subscription(s)...",
            subscriptionIds.Count);

        var storageAccounts = await DiscoverStorageAccountsAsync(subscriptionIds, cancellationToken);

        if (storageAccounts.Count == 0)
        {
            _logger.LogWarning("ARG returned 0 accounts. Trying ARM fallback...");
            storageAccounts = await FallbackArmEnumerationAsync(armClient, subscriptionIds, cancellationToken);
        }

        // Exclude the current storage account used by the solution for storing reports
        var warehouseAccount = Environment.GetEnvironmentVariable("WAREHOUSE_ACCOUNT") ?? "testconsumption92e8";
        storageAccounts = storageAccounts
            .Where(acct => !acct.Name.Equals(warehouseAccount, StringComparison.OrdinalIgnoreCase))
            .ToList();

        _logger.LogInformation("Storage accounts to scan: {Total}", storageAccounts.Count);

        // ── Step 3: Enumerate blobs ────────────────────────────────────────
        int idx = 0, failed = 0;

        foreach (var acct in storageAccounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation("[{I}/{T}] Scanning: {Name}  (rg={Rg}, sub={Sub})",
                ++idx, storageAccounts.Count, acct.Name, acct.ResourceGroup, acct.SubscriptionId);

            try
            {
                var records = await ScanAccountAsync(acct, cancellationToken);
                allRecords.AddRange(records);
                _logger.LogInformation("  → {N} blobs", records.Count);
            }
            catch (RequestFailedException rfe) when (rfe.Status is 401 or 403)
            {
                failed++;
                _logger.LogWarning(
                    "Access denied to {Account} (HTTP {Status}). " +
                    "Ensure MI has Storage Blob Data Reader.",
                    acct.Name, rfe.Status);
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "Failed to scan {Account}. Skipping.", acct.Name);
            }
        }

        _logger.LogInformation(
            "Inventory complete. Successful={Ok} Failed={Fail} TotalBlobs={Blobs} Elapsed={Elapsed}",
            idx - failed, failed, allRecords.Count, DateTimeOffset.UtcNow - startTime);

        return allRecords;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ARG Discovery — direct REST call (avoids SDK version naming issues)
    // ─────────────────────────────────────────────────────────────────────────
    private async Task<List<AccountInfo>> DiscoverStorageAccountsAsync(
        List<string> subscriptionIds, CancellationToken ct)
    {
        try
        {
            // Acquire a bearer token for management.azure.com
            var tokenRequestContext = new TokenRequestContext(
                new[] { "https://management.azure.com/.default" });
            var token = await _credential.GetTokenAsync(tokenRequestContext, ct);

            // Build ARG request body
            var body = new
            {
                query = ArgKql,
                subscriptions = subscriptionIds,
                options = new { resultFormat = "objectArray", top = 1000 }
            };
            var jsonBody = JsonSerializer.Serialize(body);

            return await RetryHelper.ExecuteWithRetryAsync(
                async () =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, ArgEndpoint);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
                    request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                    using var response = await HttpClient.SendAsync(request, ct);
                    response.EnsureSuccessStatusCode();

                    var responseJson = await response.Content.ReadAsStringAsync(ct);
                    return ParseArgResponse(responseJson);
                },
                "ARG storage account discovery",
                _logger, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ARG query failed. Will use ARM fallback.");
            return new List<AccountInfo>();
        }
    }

    private static List<AccountInfo> ParseArgResponse(string responseJson)
    {
        var list = new List<AccountInfo>();
        try
        {
            using var doc = JsonDocument.Parse(responseJson);

            // Response format: { "data": [ {...}, {...} ], "totalRecords": N, ... }
            if (!doc.RootElement.TryGetProperty("data", out var dataEl))
                return list;

            if (dataEl.ValueKind != JsonValueKind.Array) return list;

            foreach (var el in dataEl.EnumerateArray())
            {
                var name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var rg   = el.TryGetProperty("resourceGroup", out var r) ? r.GetString() ?? "" : "";
                var sub  = el.TryGetProperty("subscriptionId", out var s) ? s.GetString() ?? "" : "";

                if (!string.IsNullOrWhiteSpace(name))
                    list.Add(new AccountInfo(name, sub, rg));
            }
        }
        catch (JsonException)
        {
            // Non-fatal — caller falls back to ARM enumeration
        }
        return list;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ARM Fallback — enumerate storage accounts per subscription directly
    // ─────────────────────────────────────────────────────────────────────────
    private async Task<List<AccountInfo>> FallbackArmEnumerationAsync(
        ArmClient armClient, List<string> subscriptionIds, CancellationToken ct)
    {
        _logger.LogInformation("Using ARM direct subscription enumeration...");
        var list = new List<AccountInfo>();

        foreach (var subId in subscriptionIds)
        {
            var sub = armClient.GetSubscriptionResource(
                new ResourceIdentifier($"/subscriptions/{subId}"));

            try
            {
                await foreach (var acct in sub.GetStorageAccountsAsync(cancellationToken: ct))
                {
                    list.Add(new AccountInfo(
                        acct.Data.Name,
                        subId,
                        acct.Id.ResourceGroupName ?? "unknown"));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ARM fallback failed for subscription {Sub}", subId);
            }
        }

        return list;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Blob enumeration for one storage account
    // ─────────────────────────────────────────────────────────────────────────
    private async Task<List<BlobInventoryRecord>> ScanAccountAsync(
        AccountInfo acct, CancellationToken ct)
    {
        var records = new List<BlobInventoryRecord>();

        var svc = new BlobServiceClient(
            new Uri($"https://{acct.Name}.blob.core.windows.net"),
            _credential);

        // Enumerate containers
        var containers = new List<BlobContainerItem>();
        await RetryHelper.ExecuteWithRetryAsync(async () =>
        {
            await foreach (var c in svc.GetBlobContainersAsync(cancellationToken: ct))
                containers.Add(c);
        }, $"GetContainers({acct.Name})", _logger, ct);

        foreach (var container in containers)
        {
            if (ShouldSkip(container.Name)) continue;

            var cc = svc.GetBlobContainerClient(container.Name);
            await RetryHelper.ExecuteWithRetryAsync(async () =>
            {
                await foreach (var blob in cc.GetBlobsAsync(cancellationToken: ct))
                {
                    records.Add(new BlobInventoryRecord
                    {
                        SubscriptionId    = acct.SubscriptionId,
                        ResourceGroupName = acct.ResourceGroup,
                        StorageAccountName = acct.Name,
                        ContainerName     = container.Name,
                        BlobName          = blob.Name,
                        BlobSizeBytes     = blob.Properties.ContentLength ?? 0L,
                        AccessTier        = blob.Properties.AccessTier?.ToString() ?? InferTier(blob),
                        CreationDate      = blob.Properties.CreatedOn,
                        LastModifiedDate  = blob.Properties.LastModified,
                    });
                }
            }, $"GetBlobs({acct.Name}/{container.Name})", _logger, ct);
        }

        return records;
    }

    private static bool ShouldSkip(string name)
    {
        if (ExcludedExact.Contains(name)) return true;
        foreach (var p in ExcludedPrefixes)
            if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string InferTier(BlobItem b) =>
        b.Properties.BlobType == BlobType.Block ? "Hot (account default)" : "N/A";

    private sealed record AccountInfo(string Name, string SubscriptionId, string ResourceGroup);
}
