using BlobInventoryDotNet.Models;

namespace BlobInventoryDotNet.Services;

/// <summary>
/// Contract for the blob inventory collection service.
/// </summary>
public interface IInventoryService
{
    /// <summary>
    /// Discovers and enumerates all accessible blob storage accounts and containers
    /// across all subscriptions reachable via the configured credential (Managed Identity).
    /// </summary>
    /// <param name="subscriptionId">The optional subscription ID to scope the inventory to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// An immutable list of <see cref="BlobInventoryRecord"/> objects,
    /// one per blob discovered.
    /// </returns>
    Task<IReadOnlyList<BlobInventoryRecord>> CollectInventoryAsync(
        string? subscriptionId = default,
        CancellationToken cancellationToken = default);
}
