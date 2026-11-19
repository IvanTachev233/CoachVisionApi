using CoachVision.Data;
using CoachVision.Data.Security;
using CoachVision.Data.Tenancy;
using Microsoft.EntityFrameworkCore;

// There is no admin endpoint in the contract, so this is how a tenant is created, a key is issued
// (printed once, here, and nowhere else) and a key is revoked. Connects with the same
// ConnectionStrings:VisionDb the API uses — the run-time role, not the migrator: every operation
// here is an ordinary insert or update that row security already allows that role to make under
// its own tenant scope. Never prints or logs a key except the one line issue-key exists to print,
// and never prints the connection string itself.
return await Cli.RunAsync(args, Environment.GetEnvironmentVariable("ConnectionStrings__VisionDb"));

/// <summary>Extracted so argument handling and exit codes can be exercised without a real database.</summary>
public static class Cli
{
    public static async Task<int> RunAsync(string[] args, string? connectionString)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        if (string.IsNullOrEmpty(connectionString))
        {
            Console.Error.WriteLine("Set ConnectionStrings__VisionDb (the same value the API uses) before running this tool.");
            return 1;
        }

        try
        {
            var options = new DbContextOptionsBuilder<VisionDbContext>().UseNpgsql(connectionString).Options;
            using var db = new VisionDbContext(options);

            switch (args[0])
            {
                case "create-tenant" when args.Length == 2:
                    var tenant = await ApiKeyService.ProvisionTenantAsync(db, args[1]);
                    Console.WriteLine($"tenant_id={tenant.Id}");
                    return 0;

                case "create-tenant" when args.Length == 3 && args[2] == "--training-allowed":
                    var trainingTenant = await ApiKeyService.ProvisionTenantAsync(db, args[1], trainingAllowed: true);
                    Console.WriteLine($"tenant_id={trainingTenant.Id}");
                    return 0;

                case "set-tenant-training" when args.Length == 3 && Guid.TryParse(args[1], out var trainingTenantId) && IsValidBool(args[2]):
                    await using (var transaction = await db.BeginTenantScopeAsync(trainingTenantId))
                    {
                        await ApiKeyService.SetTenantTrainingAsync(db, trainingTenantId, bool.Parse(args[2]));
                        await transaction.CommitAsync();
                    }
                    Console.WriteLine($"tenant_id={args[1]} training_allowed={args[2]}");
                    return 0;

                case "issue-key" when args.Length == 3 && Guid.TryParse(args[1], out var issueTenantId) && IsValidKind(args[2]):
                    var (apiKey, plaintext) = await ApiKeyService.IssueAsync(db, issueTenantId, args[2]);
                    Console.WriteLine("The key below is shown once and is not recoverable afterwards. Store it now.");
                    Console.WriteLine($"api_key_id={apiKey.Id}");
                    Console.WriteLine($"key={plaintext}");
                    return 0;

                case "revoke-key" when args.Length == 3 && Guid.TryParse(args[1], out var revokeTenantId) && Guid.TryParse(args[2], out var apiKeyId):
                    await using (var transaction = await db.BeginTenantScopeAsync(revokeTenantId))
                    {
                        var key = await db.ApiKeys.SingleOrDefaultAsync(k => k.Id == apiKeyId);
                        if (key is null)
                        {
                            Console.Error.WriteLine($"No such key {apiKeyId} for tenant {revokeTenantId}.");
                            return 1;
                        }

                        if (key.RevokedAtUtc is not null)
                        {
                            Console.Error.WriteLine($"Key {apiKeyId} is already revoked.");
                            return 1;
                        }

                        await ApiKeyService.RevokeAsync(db, apiKeyId);
                        await transaction.CommitAsync();
                    }
                    Console.WriteLine($"revoked api_key_id={args[2]}");
                    return 0;

                default:
                    PrintUsage();
                    return 1;
            }
        }
        catch (ApiKeyRotationLimitExceededException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            // Never the connection string: only the exception's own one-line message, which
            // Npgsql/EF Core failures don't put a credential in.
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static bool IsValidKind(string kind) => kind is ApiKeySecrets.LiveKind or ApiKeySecrets.TestKind;

    private static bool IsValidBool(string value) => value is "true" or "false";

    private static void PrintUsage()
    {
        Console.Error.WriteLine("""
            Usage:
              create-tenant <name> [--training-allowed]
              set-tenant-training <tenant-id> <true|false>
              issue-key <tenant-id> <live|test>
              revoke-key <tenant-id> <api-key-id>

            A tenant's training flag defaults to false (every tenant, including
            external ones, starts off); set-tenant-training is the only way to turn
            it on later.

            Reads the connection string from ConnectionStrings__VisionDb (the same one
            the API uses).
            """);
    }
}
