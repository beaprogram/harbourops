namespace HarbourOps.Tests.Infrastructure;

/// <summary>Seeded fictional accounts and records used by the integration tests.</summary>
public static class DemoAccounts
{
    public const string Password = "Test-Only-Password-123!";

    // Organization A: Harbourview Property Group
    public const string MorganManager = "manager@harbourview.example";
    public const string SamTechnician = "tech.sam@harbourview.example";
    public const string JordanTechnician = "tech.jordan@harbourview.example";
    public const string RileyRequester = "resident.riley@harbourview.example";
    public const string AlexRequester = "resident.alex@harbourview.example";

    // Organization B: Cove Commons Housing Co-op
    public const string DanaManager = "manager@covecommons.example";
    public const string CaseyTechnician = "tech.casey@covecommons.example";
    public const string TaylorRequester = "resident.taylor@covecommons.example";

    public static readonly Guid HarbourviewId = Guid.Parse("10000000-0000-4000-8000-000000000001");
    public static readonly Guid CoveCommonsId = Guid.Parse("10000000-0000-4000-8000-000000000002");

    public static readonly Guid SamId = Guid.Parse("20000000-0000-4000-8000-000000000012");
    public static readonly Guid JordanId = Guid.Parse("20000000-0000-4000-8000-000000000013");
    public static readonly Guid RileyId = Guid.Parse("20000000-0000-4000-8000-000000000014");
    public static readonly Guid AlexId = Guid.Parse("20000000-0000-4000-8000-000000000015");
    public static readonly Guid CaseyId = Guid.Parse("20000000-0000-4000-8000-000000000022");

    public static readonly Guid QuaysideLofts = Guid.Parse("30000000-0000-4000-8000-000000000011");
    public static readonly Guid TidewaterCourt = Guid.Parse("30000000-0000-4000-8000-000000000012");
    public static readonly Guid CommonsWalk = Guid.Parse("30000000-0000-4000-8000-000000000021");

    /// <summary>A seeded Harbourview request created by Alex (not Riley).</summary>
    public static readonly Guid AlexHeatingRequest = Guid.Parse("40000000-0000-4000-8000-000000000103");

    /// <summary>A seeded Cove Commons request.</summary>
    public static readonly Guid CoveWindowRequest = Guid.Parse("40000000-0000-4000-8000-000000000201");
}
