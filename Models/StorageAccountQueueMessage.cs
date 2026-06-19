namespace BlobInventoryDotNet.Models;

public class StorageAccountQueueMessage
{
    public string Name { get; set; } = string.Empty;
    public string SubscriptionId { get; set; } = string.Empty;
    public string ResourceGroup { get; set; } = string.Empty;
}
