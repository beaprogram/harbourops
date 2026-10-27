using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HarbourOps.Api.Features.Attachments.Storage;

/// <summary>
/// Reports photo storage problems as Degraded, not Unhealthy: the rest of the app (requests, comments, workflow)
/// still works without photos, so the host shouldn't take the instance out of service for it.
/// </summary>
public sealed class AttachmentStorageHealthCheck(IAttachmentStorage storage) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var reachable = storage switch
            {
                AzureBlobAttachmentStorage blob => (await blob.Container.ExistsAsync(cancellationToken)).Value,
                LocalFileAttachmentStorage local => Directory.Exists(local.RootPath),
                _ => true,
            };
            return reachable ? HealthCheckResult.Healthy() : HealthCheckResult.Degraded("Photo storage is missing.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HealthCheckResult.Degraded($"Photo storage unavailable ({exception.GetType().Name}).");
        }
    }
}
