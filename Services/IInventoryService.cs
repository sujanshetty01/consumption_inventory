 using BlobInventoryDotNet.Models;

namespace BlobInventoryDotNet.Services;

/// <summary>
/// Contract for the blob inventory collection service.
/// </summary>
public interface IInventoryService
{
    /// <summary>
    /// Discovers all accessible storage accounts across subscriptions.
    /// </summary>
    Task<List<StorageAccountQueueMessage>> DiscoverStorageAccountsAsync(string? subscriptionId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enumerates blobs for a specific storage account.
    /// </summary>
    Task<List<BlobInventoryRecord>> ScanAccountAsync(StorageAccountQueueMessage acct, DateTimeOffset? lastScannedTime = null, CancellationToken ct = default);
}
