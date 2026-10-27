using System.Buffers.Text;
using System.Security.Cryptography;
using HarbourOps.Api.Common;
using HarbourOps.Api.Data;
using HarbourOps.Api.Features.Auth;
using HarbourOps.Api.Features.Organizations;
using HarbourOps.Api.Features.Properties;
using HarbourOps.Api.Features.Users;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HarbourOps.Api.Features.Demo.Sandbox;

public sealed record DemoPersonaOption(string Key, string DisplayName, UserRole Role, string OrganizationName, string Description);

public sealed record DemoUsage(
    int WorkOrders, int MaxWorkOrders, int Comments, int MaxComments, int Attachments, int MaxAttachments,
    long StoredBytes, long MaxStoredBytes, int Exports, int MaxExports);

public sealed record DemoSandboxSession(
    DateTimeOffset ExpiresAt, string? CurrentPersona, IReadOnlyList<DemoPersonaOption> Personas, DemoUsage Usage);

/// <summary>
/// <paramref name="Status"/> is "none" (no demo in this browser), "active", or "ended" (expired, reset elsewhere,
/// or ended). <paramref name="Session"/> is only present while active.
/// </summary>
public sealed record DemoSessionResponse(string Status, int LifetimeMinutes, DemoSandboxSession? Session);

public sealed record DemoSignInResponse(CurrentUserResponse User, DemoSessionResponse Session);

public sealed record SwitchPersonaRequest(string? Persona);

/// <summary>
/// Creates, resumes, switches, resets, and ends visitors' demo sandboxes.
///
/// Ownership: the browser holds a 256-bit random credential in an HttpOnly, SameSite=Strict cookie scoped to
/// /api/demo. Only its SHA-256 hash is stored. A sandbox or persona is never chosen by an ID from the client:
/// the credential identifies the sandbox and a fixed persona key picks one of its predefined people.
///
/// Signing in uses the ordinary Identity cookie for the chosen persona, so every normal authorization rule,
/// the antiforgery check, and organization isolation apply unchanged. The ticket expires with the sandbox and
/// is not renewed by activity; CurrentUserMiddleware also re-checks the sandbox on every request.
/// </summary>
public sealed class DemoSandboxService(
    AppDbContext db,
    UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signIn,
    TimeProvider clock,
    DemoCleanupSignal cleanup,
    IOptions<DemoSandboxOptions> options,
    IOptions<SecurityOptions> security,
    ILogger<DemoSandboxService> logger)
{
    public const string CredentialCookie = "HarbourOps.Demo";
    public const string DefaultPersona = "manager";

    // Arbitrary constant: serializes the capacity check and the insert of a new sandbox across requests and instances.
    private const long CapacityLockKey = 724_002;

    private sealed record PersonaTemplate(string Key, Guid TemplateUserId, string Description);

    private static readonly PersonaTemplate[] Personas =
    [
        new("manager", DemoData.MorganManagerId, "Set priorities, assign technicians, close work, and use the dashboard and export."),
        new("technician", DemoData.SamTechId, "Start and resolve the jobs assigned to Sam."),
        new("requester", DemoData.RileyRequesterId, "Report problems, add photos, and follow your own requests."),
        new("otherManager", DemoData.DanaManagerId, "A manager in a different organization: none of Harbourview's work is visible."),
    ];

    public static readonly IReadOnlyList<string> PersonaKeys = Personas.Select(p => p.Key).ToArray();

    private static readonly Failure MissingSession = new(FailureKind.Unauthenticated,
        "There's no demo session in this browser. Start a new demo.", Code: "demo_session_missing");

    public static readonly Failure AtCapacity = new(FailureKind.Unavailable,
        "The demo is busy right now. Please try again in a few minutes.", Code: "demo_capacity_reached");

    private DemoSandboxOptions Options => options.Value;

    public async Task<DemoSessionResponse> GetSessionAsync(HttpContext http, CancellationToken ct)
    {
        var sandbox = await FindByCredentialAsync(http, ct);
        if (sandbox is null)
        {
            return new DemoSessionResponse("none", LifetimeMinutes, null);
        }

        return IsActive(sandbox)
            ? new DemoSessionResponse("active", LifetimeMinutes, await DescribeAsync(sandbox, CurrentUserOf(http), ct))
            : new DemoSessionResponse("ended", LifetimeMinutes, null);
    }

    /// <summary>"Try the demo": resumes this browser's active sandbox, or creates a new one.</summary>
    public async Task<Result<DemoSignInResponse>> StartAsync(HttpContext http, CancellationToken ct)
    {
        var existing = await FindByCredentialAsync(http, ct);
        if (existing is not null && IsActive(existing))
        {
            var resumeAs = await PersonaKeyOfAsync(existing.Id, CurrentUserOf(http), ct) ?? DefaultPersona;
            return await SignInAsPersonaAsync(http, existing, resumeAs, ct);
        }

        return await CreateAndSignInAsync(http, replacing: null, DefaultPersona, ct);
    }

    public async Task<Result<DemoSignInResponse>> SwitchAsync(HttpContext http, string? persona, CancellationToken ct)
    {
        if (persona is null || !PersonaKeys.Contains(persona))
        {
            return Failure.Validation("persona", "Choose one of this demo's people.");
        }

        var sandbox = await FindByCredentialAsync(http, ct);
        if (sandbox is null)
        {
            return MissingSession;
        }

        return IsActive(sandbox) ? await SignInAsPersonaAsync(http, sandbox, persona, ct) : DemoQuotas.Ended;
    }

    /// <summary>
    /// Replaces this browser's sandbox with a fresh one. Only the caller's own sandbox (identified by its credential)
    /// is revoked; its data is removed by the cleanup job. The visitor stays on the same persona.
    /// </summary>
    public async Task<Result<DemoSignInResponse>> ResetAsync(HttpContext http, CancellationToken ct)
    {
        var sandbox = await FindByCredentialAsync(http, ct);
        if (sandbox is null)
        {
            return MissingSession;
        }

        if (!IsActive(sandbox))
        {
            return DemoQuotas.Ended;
        }

        var persona = await PersonaKeyOfAsync(sandbox.Id, CurrentUserOf(http), ct) ?? DefaultPersona;
        var result = await CreateAndSignInAsync(http, replacing: sandbox.Id, persona, ct);
        if (result.IsSuccess)
        {
            logger.LogInformation("Demo sandbox {SandboxId} reset by its visitor", sandbox.Id);
        }

        return result;
    }

    /// <summary>Ends this browser's demo: revokes the sandbox, signs the persona out, and forgets the credential.</summary>
    public async Task EndAsync(HttpContext http, CancellationToken ct)
    {
        var sandbox = await FindByCredentialAsync(http, ct);
        if (sandbox is not null && sandbox.RevokedAt is null)
        {
            await RevokeAsync(sandbox.Id, "ended", CancellationToken.None);
            logger.LogInformation("Demo sandbox {SandboxId} ended by its visitor", sandbox.Id);
        }

        // Only a demo persona (or a session that just turned out to be an ended demo) is signed out here.
        if (CurrentUserOf(http) is { IsDemo: true } || http.Items.ContainsKey(CurrentUserMiddleware.DemoEndedItem))
        {
            await signIn.SignOutAsync();
        }

        http.Response.Cookies.Delete(CredentialCookie, CredentialCookieOptions(http, expires: null));
        cleanup.Request();
    }

    private async Task<Result<DemoSignInResponse>> CreateAndSignInAsync(
        HttpContext http, Guid? replacing, string persona, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var credentialBytes = RandomNumberGenerator.GetBytes(32);
        var sandbox = new DemoSandbox
        {
            Id = Guid.CreateVersion7(now),
            CredentialHash = SHA256.HashData(credentialBytes),
            CreatedAt = now,
            ExpiresAt = now + Options.Lifetime,
        };

        // 1. Reserve: capacity check and insert under one advisory lock, so concurrent requests can't both take
        //    the last slot. A reset revokes the old sandbox in the same transaction (it no longer counts).
        await using (var reservation = await db.Database.BeginTransactionAsync(ct))
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({CapacityLockKey})", ct);
            var excluded = replacing ?? Guid.Empty;
            if (replacing is { } old)
            {
                await db.DemoSandboxes.Where(s => s.Id == old && s.RevokedAt == null)
                    .ExecuteUpdateAsync(u => u.SetProperty(s => s.RevokedAt, now).SetProperty(s => s.RevokedReason, "reset"), ct);
            }

            var holdingData = await db.DemoSandboxes.CountAsync(s => s.CleanedAt == null && s.Id != excluded, ct);
            var createdLastHour = await db.DemoSandboxes.CountAsync(s => s.CreatedAt > now.AddHours(-1), ct);
            if (holdingData >= Options.MaxActiveSandboxes || createdLastHour >= Options.MaxCreationsPerHour)
            {
                logger.LogWarning("Demo sandbox refused: {Holding} holding data, {Recent} created in the last hour", holdingData, createdLastHour);
                return AtCapacity; // rolls back, so a reset that can't proceed leaves the old sandbox untouched
            }

            db.DemoSandboxes.Add(sandbox);
            await db.SaveChangesAsync(ct);
            await reservation.CommitAsync(ct);
        }

        // 2. Seed the visitor's own fictional organizations. If this fails (or the visitor disconnects), the
        //    reservation is revoked so the cleanup job removes anything left behind.
        try
        {
            await using var seeding = await db.Database.BeginTransactionAsync(ct);
            await SeedAsync(sandbox, now, ct);
            await seeding.CommitAsync(ct);
        }
        catch (Exception exception)
        {
            db.ChangeTracker.Clear();
            await RevokeAsync(sandbox.Id, "failed", CancellationToken.None);
            cleanup.Request();
            if (exception is OperationCanceledException)
            {
                throw;
            }

            logger.LogError(exception, "Seeding demo sandbox {SandboxId} failed", sandbox.Id);
            return new Failure(FailureKind.Unavailable, "The demo couldn't be prepared. Please try again.", Code: "demo_unavailable");
        }

        // 3. Hand the credential to this browser only now that the sandbox is complete.
        http.Response.Cookies.Append(
            CredentialCookie, Base64Url.EncodeToString(credentialBytes),
            CredentialCookieOptions(http, sandbox.ExpiresAt + Options.TombstoneRetention));
        if (replacing is not null)
        {
            cleanup.Request();
        }

        logger.LogInformation("Demo sandbox {SandboxId} created, expires {ExpiresAt}", sandbox.Id, sandbox.ExpiresAt);
        return await SignInAsPersonaAsync(http, sandbox, persona, ct);
    }

    private async Task<Result<DemoSignInResponse>> SignInAsPersonaAsync(
        HttpContext http, DemoSandbox sandbox, string personaKey, CancellationToken ct)
    {
        // The persona must belong to *this* sandbox, and its organization must too (defence in depth: the
        // composite key on DemoPersonas already ties the user to its organization).
        var user = await db.DemoPersonas
            .Where(p => p.SandboxId == sandbox.Id && p.Key == personaKey)
            .Join(db.Users, p => p.UserId, u => u.Id, (p, u) => u)
            .Where(u => u.IsActive && u.Organization.DemoSandboxId == sandbox.Id)
            .Include(u => u.Organization)
            .SingleOrDefaultAsync(ct);
        if (user is null)
        {
            return DemoQuotas.Ended;
        }

        await signIn.SignInAsync(user, new AuthenticationProperties
        {
            IsPersistent = false,
            ExpiresUtc = sandbox.ExpiresAt, // the session can never outlive the sandbox ...
            AllowRefresh = false,           // ... and sliding expiration must not extend it
        });
        logger.LogInformation("Demo sandbox {SandboxId}: signed in as persona {Persona}", sandbox.Id, personaKey);

        var current = new CurrentUser(user.Id, user.Email!, user.DisplayName, user.Role, user.OrganizationId,
            user.Organization.Name, sandbox.Id, sandbox.ExpiresAt);
        return new DemoSignInResponse(
            AuthEndpoints.ToResponse(current),
            new DemoSessionResponse("active", LifetimeMinutes, await DescribeAsync(sandbox, current, ct)));
    }

    private async Task SeedAsync(DemoSandbox sandbox, DateTimeOffset now, CancellationToken ct)
    {
        var ids = new Dictionary<Guid, Guid>();
        Guid Map(Guid templateId) => ids.TryGetValue(templateId, out var id) ? id : ids[templateId] = Guid.CreateVersion7(now);
        var suffix = sandbox.Id.ToString("N")[^12..];
        string EmailOf(DemoUser user)
        {
            var at = user.Email.IndexOf('@');
            return $"{user.Email[..at]}.{user.Email[(at + 1)..].Split('.')[0]}.{suffix}@sandbox.harbourops.example";
        }

        foreach (var org in DemoData.Organizations)
        {
            db.Organizations.Add(new Organization
            {
                Id = Map(org.Id), Name = org.Name, Slug = $"{org.Slug}-{suffix}", CreatedAt = now, DemoSandboxId = sandbox.Id,
            });
        }

        foreach (var property in DemoData.Properties)
        {
            db.Properties.Add(new Property
            {
                Id = Map(property.Id), OrganizationId = Map(property.OrganizationId), Name = property.Name,
                AddressLine = property.AddressLine, Community = property.Community,
            });
        }

        await db.SaveChangesAsync(ct);

        foreach (var template in DemoData.Users)
        {
            var email = EmailOf(template);
            // No password: a persona can only be reached through its sandbox's credential (see AuthEndpoints too).
            var result = await users.CreateAsync(new ApplicationUser
            {
                Id = Map(template.Id), OrganizationId = Map(template.OrganizationId), UserName = email, Email = email,
                EmailConfirmed = true, DisplayName = template.DisplayName, Role = template.Role,
            });
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Could not create demo persona: {string.Join("; ", result.Errors.Select(e => e.Description))}");
            }
        }

        for (var i = 0; i < Personas.Length; i++)
        {
            var template = DemoData.Users.Single(u => u.Id == Personas[i].TemplateUserId);
            db.DemoPersonas.Add(new DemoPersona
            {
                SandboxId = sandbox.Id, Key = Personas[i].Key, OrganizationId = Map(template.OrganizationId),
                UserId = Map(template.Id), SortOrder = i,
            });
        }

        CurrentUser Actor(Guid templateUserId)
        {
            var user = DemoData.Users.Single(u => u.Id == templateUserId);
            var org = DemoData.Organizations.Single(o => o.Id == user.OrganizationId);
            return new CurrentUser(Map(user.Id), EmailOf(user), user.DisplayName, user.Role, Map(org.Id), org.Name, sandbox.Id, sandbox.ExpiresAt);
        }

        foreach (var demo in DemoData.WorkOrders)
        {
            var (workOrder, events) = DemoDataSeeder.ReplayWorkOrder(demo, Map, Actor, now);
            db.WorkOrders.Add(workOrder);
            db.AuditEvents.AddRange(events);
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<DemoSandboxSession> DescribeAsync(DemoSandbox sandbox, CurrentUser? signedIn, CancellationToken ct)
    {
        var people = await db.DemoPersonas.AsNoTracking()
            .Where(p => p.SandboxId == sandbox.Id)
            .OrderBy(p => p.SortOrder)
            .Join(db.Users, p => p.UserId, u => u.Id, (p, u) => new { p.Key, u.Id, u.DisplayName, u.Role, Organization = u.Organization.Name })
            .ToListAsync(ct);
        var options = people.Select(p => new DemoPersonaOption(
            p.Key, p.DisplayName, p.Role, p.Organization, Personas.Single(t => t.Key == p.Key).Description)).ToList();
        var current = signedIn is null ? null : people.FirstOrDefault(p => p.Id == signedIn.Id)?.Key;
        var o = Options;
        return new DemoSandboxSession(sandbox.ExpiresAt, current, options, new DemoUsage(
            sandbox.WorkOrderCount, o.MaxWorkOrders, sandbox.CommentCount, o.MaxComments, sandbox.AttachmentCount,
            o.MaxAttachments, sandbox.StoredBytes, o.MaxStoredBytes, sandbox.ExportCount, o.MaxExports));
    }

    private async Task<string?> PersonaKeyOfAsync(Guid sandboxId, CurrentUser? user, CancellationToken ct) =>
        user is null
            ? null
            : await db.DemoPersonas.Where(p => p.SandboxId == sandboxId && p.UserId == user.Id).Select(p => p.Key).SingleOrDefaultAsync(ct);

    private async Task<DemoSandbox?> FindByCredentialAsync(HttpContext http, CancellationToken ct)
    {
        var value = http.Request.Cookies[CredentialCookie];
        Span<byte> bytes = stackalloc byte[32];
        if (value is not { Length: 43 } || !Base64Url.TryDecodeFromChars(value, bytes, out var written) || written != 32)
        {
            return null;
        }

        var hash = SHA256.HashData(bytes);
        return await db.DemoSandboxes.AsNoTracking().SingleOrDefaultAsync(s => s.CredentialHash == hash, ct);
    }

    private Task RevokeAsync(Guid sandboxId, string reason, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return db.DemoSandboxes.Where(s => s.Id == sandboxId && s.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.RevokedAt, now).SetProperty(s => s.RevokedReason, reason), ct);
    }

    private bool IsActive(DemoSandbox sandbox) => sandbox.RevokedAt is null && sandbox.ExpiresAt > clock.GetUtcNow();

    private int LifetimeMinutes => (int)Options.Lifetime.TotalMinutes;

    private static CurrentUser? CurrentUserOf(HttpContext http) => http.Items[typeof(CurrentUser)] as CurrentUser;

    private CookieOptions CredentialCookieOptions(HttpContext http, DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Path = "/api/demo", // only the demo endpoints ever need it
        IsEssential = true,
        Secure = security.Value.CookieSecurePolicy switch
        {
            CookieSecurePolicy.Always => true,
            CookieSecurePolicy.SameAsRequest => http.Request.IsHttps,
            _ => false,
        },
        Expires = expires,
    };
}
