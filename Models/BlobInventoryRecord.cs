namespace BlobInventoryDotNet.Models;

/// <summary>
/// Represents a single blob's inventory record.
/// Contains the 9 fields required by the specification.
/// </summary>
public sealed class BlobInventoryRecord
{
    /// <summary>Azure Subscription ID where the blob resides.</summary>
    public string SubscriptionId { get; init; } = string.Empty;

    /// <summary>Resource Group Name of the parent storage account.</summary>
    public string ResourceGroupName { get; init; } = string.Empty;

    /// <summary>Name of the Azure Storage Account.</summary>
    public string StorageAccountName { get; init; } = string.Empty;

    /// <summary>Blob container name.</summary>
    public string ContainerName { get; init; } = string.Empty;

    /// <summary>Full blob name (path within container).</summary>
    public string BlobName { get; init; } = string.Empty;

    /// <summary>Blob size in bytes.</summary>
    public long BlobSizeBytes { get; init; }

    /// <summary>Access tier (Hot, Cool, Cold, Archive, or null if unknown).</summary>
    public string? AccessTier { get; init; }

    /// <summary>Blob creation time (UTC). Null if not available.</summary>
    public DateTimeOffset? CreationDate { get; init; }

    /// <summary>Last modification time (UTC). Null if not available.</summary>
    public DateTimeOffset? LastModifiedDate { get; init; }
}
