using Polly;
using Polly.Retry;
using Azure;
using Microsoft.Extensions.Logging;

namespace BlobInventoryDotNet.Helpers;

/// <summary>
/// Provides Polly-based retry logic for Azure SDK calls.
/// Retries on transient HTTP errors (429, 500, 502, 503, 504).
/// Uses exponential backoff: 1s, 2s, 4s.
/// </summary>
public static class RetryHelper
{
    private const int MaxRetries = 3;

    /// <summary>
    /// Executes an async operation with exponential backoff retry on transient Azure failures.
    /// </summary>
    public static async Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> operation,
        string operationDescription,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        int attempt = 0;

        var pipeline = new ResiliencePipelineBuilder<T>()
            .AddRetry(new RetryStrategyOptions<T>
            {
                MaxRetryAttempts = MaxRetries,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromSeconds(1),
                UseJitter = true,
                ShouldHandle = new PredicateBuilder<T>()
                    .Handle<RequestFailedException>(ex =>
                        ex.Status == 429 ||
                        ex.Status == 500 ||
                        ex.Status == 502 ||
                        ex.Status == 503 ||
                        ex.Status == 504)
                    .Handle<OperationCanceledException>(ex =>
                        !cancellationToken.IsCancellationRequested),
                OnRetry = args =>
                {
                    attempt++;
                    logger.LogWarning(
                        "Retry {Attempt}/{Max} for '{Operation}' after {Delay:0.00}s. " +
                        "Exception: {ExMessage}",
                        attempt, MaxRetries, operationDescription,
                        args.RetryDelay.TotalSeconds,
                        args.Outcome.Exception?.Message ?? "unknown");
                    return ValueTask.CompletedTask;
                }
            })
            .Build();

        return await pipeline.ExecuteAsync(
            async ct => await operation(),
            cancellationToken);
    }

    /// <summary>
    /// Executes an async void operation with exponential backoff retry on transient Azure failures.
    /// </summary>
    public static async Task ExecuteWithRetryAsync(
        Func<Task> operation,
        string operationDescription,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        await ExecuteWithRetryAsync<bool>(
            async () => { await operation(); return true; },
            operationDescription,
            logger,
            cancellationToken);
    }
}
