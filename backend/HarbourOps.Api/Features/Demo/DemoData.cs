using HarbourOps.Api.Features.Users;
using HarbourOps.Api.Features.WorkOrders;

namespace HarbourOps.Api.Features.Demo;

internal sealed record DemoOrganization(Guid Id, string Name, string Slug);

internal sealed record DemoUser(Guid Id, Guid OrganizationId, string Email, string DisplayName, UserRole Role);

internal sealed record DemoProperty(Guid Id, Guid OrganizationId, string Name, string AddressLine, string Community);

internal sealed record DemoWorkOrder(
    Guid Id,
    Guid PropertyId,
    Guid RequesterId,
    Guid? TechnicianId,
    string Title,
    string Description,
    WorkOrderCategory Category,
    WorkOrderPriority Priority,
    WorkOrderStatus Status,
    double CreatedHoursAgo,
    string? ResolutionNote = null,
    string? ReopenReason = null);

/// <summary>
/// Entirely fictional demo data. Organization names, people, street names, and requests are invented;
/// email addresses use the reserved .example domain. Fixed IDs make seeding safe to rerun.
/// </summary>
internal static class DemoData
{
    public static readonly Guid HarbourviewId = Guid.Parse("10000000-0000-4000-8000-000000000001");
    public static readonly Guid CoveCommonsId = Guid.Parse("10000000-0000-4000-8000-000000000002");

    public static readonly DemoOrganization[] Organizations =
    [
        new(HarbourviewId, "Harbourview Property Group", "harbourview"),
        new(CoveCommonsId, "Cove Commons Housing Co-op", "cove-commons"),
    ];

    // Harbourview (organization A)
    public static readonly Guid MorganManagerId = Guid.Parse("20000000-0000-4000-8000-000000000011");
    public static readonly Guid SamTechId = Guid.Parse("20000000-0000-4000-8000-000000000012");
    public static readonly Guid JordanTechId = Guid.Parse("20000000-0000-4000-8000-000000000013");
    public static readonly Guid RileyRequesterId = Guid.Parse("20000000-0000-4000-8000-000000000014");
    public static readonly Guid AlexRequesterId = Guid.Parse("20000000-0000-4000-8000-000000000015");

    // Cove Commons (organization B)
    public static readonly Guid DanaManagerId = Guid.Parse("20000000-0000-4000-8000-000000000021");
    public static readonly Guid CaseyTechId = Guid.Parse("20000000-0000-4000-8000-000000000022");
    public static readonly Guid TaylorRequesterId = Guid.Parse("20000000-0000-4000-8000-000000000023");

    public static readonly DemoUser[] Users =
    [
        new(MorganManagerId, HarbourviewId, "manager@harbourview.example", "Morgan Ellis", UserRole.Manager),
        new(SamTechId, HarbourviewId, "tech.sam@harbourview.example", "Sam Okafor", UserRole.Technician),
        new(JordanTechId, HarbourviewId, "tech.jordan@harbourview.example", "Jordan Leblanc", UserRole.Technician),
        new(RileyRequesterId, HarbourviewId, "resident.riley@harbourview.example", "Riley Chen", UserRole.Requester),
        new(AlexRequesterId, HarbourviewId, "resident.alex@harbourview.example", "Alex MacIsaac", UserRole.Requester),
        new(DanaManagerId, CoveCommonsId, "manager@covecommons.example", "Dana Fraser", UserRole.Manager),
        new(CaseyTechId, CoveCommonsId, "tech.casey@covecommons.example", "Casey Boudreau", UserRole.Technician),
        new(TaylorRequesterId, CoveCommonsId, "resident.taylor@covecommons.example", "Taylor Singh", UserRole.Requester),
    ];

    public static readonly Guid QuaysideLoftsId = Guid.Parse("30000000-0000-4000-8000-000000000011");
    public static readonly Guid TidewaterCourtId = Guid.Parse("30000000-0000-4000-8000-000000000012");
    public static readonly Guid FerryLandingId = Guid.Parse("30000000-0000-4000-8000-000000000013");
    public static readonly Guid CommonsWalkId = Guid.Parse("30000000-0000-4000-8000-000000000021");
    public static readonly Guid LakesideGardensId = Guid.Parse("30000000-0000-4000-8000-000000000022");

    public static readonly DemoProperty[] Properties =
    [
        new(QuaysideLoftsId, HarbourviewId, "Quayside Lofts", "18 Quayside Lane", "Halifax"),
        new(TidewaterCourtId, HarbourviewId, "Tidewater Court", "240 Tidewater Court", "Halifax"),
        new(FerryLandingId, HarbourviewId, "Ferry Landing Townhomes", "7 Ferrybank Row", "Dartmouth"),
        new(CommonsWalkId, CoveCommonsId, "Commons Walk", "55 Lanternlight Walk", "Halifax"),
        new(LakesideGardensId, CoveCommonsId, "Lakeside Gardens", "312 Mistlake Crescent", "Dartmouth"),
    ];

    public static readonly DemoWorkOrder[] WorkOrders =
    [
        // Harbourview
        new(Guid.Parse("40000000-0000-4000-8000-000000000101"), QuaysideLoftsId, RileyRequesterId, null,
            "Kitchen sink draining slowly",
            "Unit 304. Water takes several minutes to drain after washing dishes. No leaks under the cabinet.",
            WorkOrderCategory.Plumbing, WorkOrderPriority.Normal, WorkOrderStatus.New, 5),
        new(Guid.Parse("40000000-0000-4000-8000-000000000102"), FerryLandingId, MorganManagerId, null,
            "Ice build-up on rear stairs",
            "Rear stairs at unit 7B are icing over where the downspout drains. Needs salting and a downspout extension.",
            WorkOrderCategory.Exterior, WorkOrderPriority.High, WorkOrderStatus.New, 3),
        new(Guid.Parse("40000000-0000-4000-8000-000000000103"), TidewaterCourtId, AlexRequesterId, SamTechId,
            "No heat from bedroom radiator",
            "Unit 112. Bedroom radiator stays cold even with the thermostat at 22°C. Living room is fine.",
            WorkOrderCategory.Heating, WorkOrderPriority.High, WorkOrderStatus.Assigned, 9),
        new(Guid.Parse("40000000-0000-4000-8000-000000000104"), QuaysideLoftsId, RileyRequesterId, JordanTechId,
            "Hallway light flickering on second floor",
            "The ceiling light outside unit 210 flickers constantly in the evening.",
            WorkOrderCategory.Electrical, WorkOrderPriority.Normal, WorkOrderStatus.InProgress, 30),
        new(Guid.Parse("40000000-0000-4000-8000-000000000105"), QuaysideLoftsId, AlexRequesterId, SamTechId,
            "Bathroom exhaust fan rattling",
            "Unit 118. Fan makes a loud rattle and barely moves air. Mirror fogs up after every shower.",
            WorkOrderCategory.Appliances, WorkOrderPriority.Normal, WorkOrderStatus.InProgress, 100,
            ResolutionNote: "Tightened the fan housing screws.",
            ReopenReason: "Still rattling after two days and the mirror still fogs."),
        new(Guid.Parse("40000000-0000-4000-8000-000000000106"), FerryLandingId, RileyRequesterId, SamTechId,
            "Dishwasher leaking onto floor",
            "Unit 3A. Small puddle forms in front of the dishwasher after each cycle.",
            WorkOrderCategory.Appliances, WorkOrderPriority.High, WorkOrderStatus.Resolved, 40,
            ResolutionNote: "Replaced the cracked door gasket and ran a test cycle. No further leaking."),
        new(Guid.Parse("40000000-0000-4000-8000-000000000107"), TidewaterCourtId, AlexRequesterId, JordanTechId,
            "Front entrance door closer broken",
            "The main entrance door slams shut and does not latch reliably.",
            WorkOrderCategory.General, WorkOrderPriority.Normal, WorkOrderStatus.Closed, 160,
            ResolutionNote: "Installed a new hydraulic door closer and adjusted the latch speed."),

        // Cove Commons
        new(Guid.Parse("40000000-0000-4000-8000-000000000201"), CommonsWalkId, TaylorRequesterId, null,
            "Living room window will not latch",
            "Unit 4. The latch does not catch, so the window drifts open in the wind.",
            WorkOrderCategory.General, WorkOrderPriority.Normal, WorkOrderStatus.New, 6),
        new(Guid.Parse("40000000-0000-4000-8000-000000000202"), LakesideGardensId, TaylorRequesterId, CaseyTechId,
            "Water heater pilot light out",
            "No hot water since this morning in unit 9. Pilot light appears to be out.",
            WorkOrderCategory.Plumbing, WorkOrderPriority.High, WorkOrderStatus.Assigned, 4),
        new(Guid.Parse("40000000-0000-4000-8000-000000000203"), LakesideGardensId, DanaManagerId, CaseyTechId,
            "Smoke detector chirping in stairwell",
            "Stairwell B smoke detector chirps every minute. Likely a low battery.",
            WorkOrderCategory.Electrical, WorkOrderPriority.Normal, WorkOrderStatus.Resolved, 20,
            ResolutionNote: "Replaced the battery and tested the detector."),
        new(Guid.Parse("40000000-0000-4000-8000-000000000204"), CommonsWalkId, TaylorRequesterId, CaseyTechId,
            "Pothole at parking lot entrance",
            "Deep pothole at the entrance is collecting water and catching tires.",
            WorkOrderCategory.Exterior, WorkOrderPriority.Normal, WorkOrderStatus.Closed, 200,
            ResolutionNote: "Filled with cold patch asphalt. Permanent repair scheduled with the spring paving."),
    ];
}
