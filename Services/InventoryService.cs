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

namespace BlobInventoryDotNet.Services;

public sealed class InventoryService : IInventoryService
{
    private static readonly string[] ExcludedPrefixes =
    {
        "azure-webjobs-",
        "app-package-",
        "scm-releases",
        // Durable Functions internal containers (leases, applease, control, etc.)
        "testfunconsumption-",
    };

    private static readonly HashSet<string> ExcludedExact =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "$logs",
            "$blobchangefeed",
            "$web",
            "$root",
            "inventory-reports",
        };

    private const string ArgEndpoint = "https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2021-03-01";

    private const string ArgKql = @"
        Resources
        | where type =~ 'microsoft.storage/storageaccounts'
        | project name, resourceGroup, subscriptionId, location
        | order by subscriptionId asc, name asc";

    private readonly TokenCredential _credential;
    private readonly ILogger<InventoryService> _logger;
    private static readonly HttpClient HttpClient = new();

    public InventoryService(TokenCredential credential, ILogger<InventoryService> logger)
    {
        _credential = credential ?? throw new ArgumentNullException(nameof(credential));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<List<StorageAccountQueueMessage>> DiscoverStorageAccountsAsync(string? subscriptionId = null, CancellationToken cancellationToken = default)
    {
        var armClient = new ArmClient(_credential);
        var subscriptionIds = new List<string>();

        if (!string.IsNullOrWhiteSpace(subscriptionId))
        {
            // Use the explicitly provided subscription ID
            _logger.LogInformation("Using explicitly provided subscription: {Sub}", subscriptionId);
            subscriptionIds.Add(subscriptionId);
        }
        else
        {
            // Fallback: enumerate all accessible subscriptions (original behavior)
            _logger.LogInformation("No subscription filter provided. Enumerating all accessible subscriptions...");
            await foreach (var sub in armClient.GetSubscriptions().GetAllAsync(cancellationToken: cancellationToken))
            {
                if (!string.IsNullOrWhiteSpace(sub.Data.SubscriptionId))
                {
                    subscriptionIds.Add(sub.Data.SubscriptionId);
                }
            }
        }

        if (subscriptionIds.Count == 0)
        {
            _logger.LogWarning("No subscriptions found.");
            return new List<StorageAccountQueueMessage>();
        }

        _logger.LogInformation("Querying Azure Resource Graph across {N} subscription(s)...", subscriptionIds.Count);
        var storageAccounts = await DiscoverViaArgAsync(subscriptionIds, cancellationToken);

        if (storageAccounts.Count == 0)
        {
            _logger.LogWarning("ARG returned 0 accounts. Trying ARM fallback...");
            storageAccounts = await FallbackArmEnumerationAsync(armClient, subscriptionIds, cancellationToken);
        }

        // Exclude the function app's own storage account to avoid reporting
        // on internal Durable Functions blobs (leases, control, taskhub, etc.).
        var excludedAccounts = GetFunctionAppStorageAccountNames();
        if (excludedAccounts.Count > 0)
        {
            var before = storageAccounts.Count;
            storageAccounts = storageAccounts
                .Where(a => !excludedAccounts.Contains(a.Name, StringComparer.OrdinalIgnoreCase))
                .ToList();
            var removed = before - storageAccounts.Count;
            if (removed > 0)
                _logger.LogInformation(
                    "Excluded {Count} function-app storage account(s) from scan: {Names}",
                    removed, string.Join(", ", excludedAccounts));
        }

        return storageAccounts;
    }

    private async Task<List<StorageAccountQueueMessage>> DiscoverViaArgAsync(List<string> subscriptionIds, CancellationToken ct)
    {
        try
        {
            var tokenRequestContext = new TokenRequestContext(new[] { "https://management.azure.com/.default" });
            var token = await _credential.GetTokenAsync(tokenRequestContext, ct);

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
            _logger.LogError(ex, "ARG query failed.");
            return new List<StorageAccountQueueMessage>();
        }
    }

    private static List<StorageAccountQueueMessage> ParseArgResponse(string responseJson)
    {
        var list = new List<StorageAccountQueueMessage>();
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (!doc.RootElement.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var el in dataEl.EnumerateArray())
            {
                var name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var rg   = el.TryGetProperty("resourceGroup", out var r) ? r.GetString() ?? "" : "";
                var sub  = el.TryGetProperty("subscriptionId", out var s) ? s.GetString() ?? "" : "";

                if (!string.IsNullOrWhiteSpace(name))
                    list.Add(new StorageAccountQueueMessage { Name = name, SubscriptionId = sub, ResourceGroup = rg });
            }
        }
        catch (JsonException)
        {
        }
        return list;
    }

    private async Task<List<StorageAccountQueueMessage>> FallbackArmEnumerationAsync(ArmClient armClient, List<string> subscriptionIds, CancellationToken ct)
    {
        var list = new List<StorageAccountQueueMessage>();
        foreach (var subId in subscriptionIds)
        {
            var sub = armClient.GetSubscriptionResource(new ResourceIdentifier($"/subscriptions/{subId}"));
            try
            {
                await foreach (var acct in sub.GetStorageAccountsAsync(cancellationToken: ct))
                {
                    list.Add(new StorageAccountQueueMessage
                    {
                        Name = acct.Data.Name,
                        SubscriptionId = subId,
                        ResourceGroup = acct.Id.ResourceGroupName ?? "unknown"
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ARM fallback failed for subscription {Sub}", subId);
            }
        }
        return list;
    }

    public async Task<List<BlobInventoryRecord>> ScanAccountAsync(StorageAccountQueueMessage acct, DateTimeOffset? lastScannedTime = null, CancellationToken ct = default)
    {
        var records = new List<BlobInventoryRecord>();
        var svc = new BlobServiceClient(new Uri($"https://{acct.Name}.blob.core.windows.net"), _credential);

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
                    if (lastScannedTime.HasValue && blob.Properties.LastModified <= lastScannedTime.Value)
                        continue;

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

    private static string InferTier(BlobItem b) => b.Properties.BlobType == BlobType.Block ? "Hot (account default)" : "N/A";

    /// <summary>
    /// Returns the set of storage account names used by the Function App itself,
    /// so they can be excluded from inventory scans.
    /// Reads from FUNCTION_APP_STORAGE_ACCOUNT env var and/or parses AzureWebJobsStorage.
    /// </summary>
    private static HashSet<string> GetFunctionAppStorageAccountNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Explicit env var (recommended for clarity)
        var explicitName = Environment.GetEnvironmentVariable("FUNCTION_APP_STORAGE_ACCOUNT");
        if (!string.IsNullOrWhiteSpace(explicitName))
            names.Add(explicitName.Trim());

        // Parse from AzureWebJobsStorage connection string
        var connStr = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
        if (!string.IsNullOrWhiteSpace(connStr))
        {
            var accountName = ParseAccountNameFromConnectionString(connStr);
            if (!string.IsNullOrWhiteSpace(accountName))
                names.Add(accountName);
        }

        return names;
    }

    /// <summary>
    /// Extracts the AccountName value from an Azure Storage connection string.
    /// </summary>
    private static string? ParseAccountNameFromConnectionString(string connectionString)
    {
        // Connection string format: "DefaultEndpointsProtocol=https;AccountName=xxx;AccountKey=yyy;..."
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("AccountName=", StringComparison.OrdinalIgnoreCase))
                return trimmed.Substring("AccountName=".Length).Trim();
        }
        return null;
    }
}
