using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HarbourOps.Api.Features.Attachments.Storage;

/// <summary>
/// Reports photo storage problems as Degraded, not Unhealthy: the rest of the app (requests, comments, workflow)
/// still works without photos, so the host shouldn't take the instance out of service for it. The one exception
/// is a Supabase bucket that has been made public: that would expose photos outside the authorized endpoints, so
/// readiness fails and a deployment with that setting does not go live.
/// </summary>
public sealed class AttachmentStorageHealthCheck(IAttachmentStorage storage) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (storage is SupabaseStorageAttachmentStorage supabase)
            {
                var bucket = await supabase.GetBucketAsync(cancellationToken);
                return bucket switch
                {
                    null => HealthCheckResult.Degraded("Photo storage is missing."),
                    { Public: true } => HealthCheckResult.Unhealthy("The photo bucket is public; it must be private."),
                    _ => HealthCheckResult.Healthy(),
                };
            }

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
