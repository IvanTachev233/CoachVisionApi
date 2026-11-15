namespace CoachVision.Data.Entities;

/// <summary>One Vision customer (LiftBig is the first). Every tenant-owned table carries this id.</summary>
public sealed class Tenant
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
