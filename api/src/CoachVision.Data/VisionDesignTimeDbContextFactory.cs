using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CoachVision.Data;

/// <summary>
/// Lets <c>dotnet ef</c> build a <see cref="VisionDbContext"/> without running the API (no
/// configuration, no web host). Used for <c>migrations add</c> (never opens the connection) and
/// for <c>database update</c> / <c>has-pending-model-changes</c> against a throwaway or
/// migrator-owned database (see the runbook); it never carries a real credential, so there is no
/// literal fallback here.
/// </summary>
public sealed class VisionDesignTimeDbContextFactory : IDesignTimeDbContextFactory<VisionDbContext>
{
    public VisionDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("VISIONDB_DESIGN_TIME_CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "Set VISIONDB_DESIGN_TIME_CONNECTION_STRING before running dotnet ef " +
                "(a throwaway local database for `migrations add`, or the migrator connection " +
                "string for `database update` / `has-pending-model-changes`).");

        var optionsBuilder = new DbContextOptionsBuilder<VisionDbContext>();
        optionsBuilder.UseNpgsql(connectionString);
        return new VisionDbContext(optionsBuilder.Options);
    }
}
