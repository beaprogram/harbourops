using HarbourOps.Api.Features.Comments;
using HarbourOps.Api.Features.Organizations;
using HarbourOps.Api.Features.Properties;
using HarbourOps.Api.Features.Users;
using HarbourOps.Api.Features.WorkOrders;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace HarbourOps.Api.Data;

/// <summary>
/// The single EF Core context. <see cref="IdentityUserContext{TUser,TKey}"/> adds the Identity
/// user tables without role tables, because each account's role is a column on the user.
/// It also stores ASP.NET Core Data Protection keys so auth cookies survive restarts and scale-out.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityUserContext<ApplicationUser, Guid>(options), IDataProtectionKeyContext
{
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Property> Properties => Set<Property>();
    public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
