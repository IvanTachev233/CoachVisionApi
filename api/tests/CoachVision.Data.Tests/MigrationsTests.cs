using CoachVision.Data;
using CoachVision.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CoachVision.Data.Tests;

[Collection(VisionDbCollection.Name)]
public sealed class MigrationsTests(VisionPostgresFixture fixture)
{
    [Fact]
    public async Task Migrations_build_an_empty_database()
    {
        using var context = VisionPostgresFixture.CreateContext(fixture.MigratorConnectionString);

        var pendingMigrations = await context.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken);

        Assert.Empty(pendingMigrations);
        Assert.NotEmpty(await context.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void The_model_has_no_pending_changes()
    {
        using var context = VisionPostgresFixture.CreateContext(fixture.MigratorConnectionString);

        // The same check `dotnet ef migrations has-pending-model-changes` runs in CI: a model
        // change without a matching migration fails here too.
        Assert.False(context.Database.HasPendingModelChanges());
    }
}
