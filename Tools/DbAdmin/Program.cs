using Npgsql;
using System.Text.Json;

var projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
var connectionString = ReadConnectionString(projectRoot)
    ?? throw new InvalidOperationException("DefaultConnection was not found.");

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

await using var conn = new NpgsqlConnection(connectionString);
await conn.OpenAsync();

switch (args[0].ToLowerInvariant())
{
    case "list-users":
        await ListUsersAsync(conn);
        return 0;

    case "delete-user":
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Missing email for delete-user.");
            PrintUsage();
            return 1;
        }

        return await DeleteUserAsync(conn, args[1]);

    default:
        Console.Error.WriteLine($"Unknown command '{args[0]}'.");
        PrintUsage();
        return 1;
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project Tools/DbAdmin -- list-users");
    Console.WriteLine("  dotnet run --project Tools/DbAdmin -- delete-user <email>");
}

static string? ReadConnectionString(string projectRoot)
{
    foreach (var fileName in new[] { "appsettings.Development.json", "appsettings.json" })
    {
        var path = Path.Combine(projectRoot, fileName);
        if (!File.Exists(path))
        {
            continue;
        }

        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        if (document.RootElement.TryGetProperty("ConnectionStrings", out var connectionStrings) &&
            connectionStrings.TryGetProperty("DefaultConnection", out var value))
        {
            return value.GetString();
        }
    }

    return null;
}

static async Task ListUsersAsync(NpgsqlConnection conn)
{
    const string sql = """
        select "Id", "Email", "Name", "AuthProvider", "CreatedAt"
        from "Users"
        order by "CreatedAt" desc
        """;

    await using var cmd = new NpgsqlCommand(sql, conn);
    await using var reader = await cmd.ExecuteReaderAsync();

    while (await reader.ReadAsync())
    {
        var id = reader.GetGuid(0);
        var email = reader.GetString(1);
        var name = reader.GetString(2);
        var authProvider = reader.IsDBNull(3) ? "password" : reader.GetString(3);
        var createdAt = reader.GetDateTime(4);
        Console.WriteLine($"{id}\t{email}\t{name}\t{authProvider}\t{createdAt:O}");
    }
}

static async Task<int> DeleteUserAsync(NpgsqlConnection conn, string email)
{
    var normalizedEmail = email.Trim().ToLowerInvariant();

    const string lookupSql = """
        select "Id", "Email", "Name"
        from "Users"
        where lower("Email") = @email
        """;

    await using var lookup = new NpgsqlCommand(lookupSql, conn);
    lookup.Parameters.AddWithValue("email", normalizedEmail);

    Guid userId;
    string userEmail;
    string userName;

    await using (var reader = await lookup.ExecuteReaderAsync())
    {
        if (!await reader.ReadAsync())
        {
            Console.Error.WriteLine($"No user found for {normalizedEmail}.");
            return 1;
        }

        userId = reader.GetGuid(0);
        userEmail = reader.GetString(1);
        userName = reader.GetString(2);
    }

    await using var transaction = await conn.BeginTransactionAsync();

    var ownedTripIds = new List<Guid>();
    const string ownedTripsSql = """
        select "Id"
        from "Trips"
        where "OwnerId" = @userId
        """;

    await using (var ownedTrips = new NpgsqlCommand(ownedTripsSql, conn, transaction))
    {
        ownedTrips.Parameters.AddWithValue("userId", userId);
        await using var reader = await ownedTrips.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ownedTripIds.Add(reader.GetGuid(0));
        }
    }

    if (ownedTripIds.Count > 0)
    {
        const string deleteActivitiesSql = """
            delete from "TripActivities"
            where "TripId" = any(@tripIds)
            """;

        const string deleteMembersSql = """
            delete from "TripMembers"
            where "TripId" = any(@tripIds)
            """;

        const string deleteTripsSql = """
            delete from "Trips"
            where "Id" = any(@tripIds)
            """;

        await using (var deleteActivities = new NpgsqlCommand(deleteActivitiesSql, conn, transaction))
        {
            deleteActivities.Parameters.AddWithValue("tripIds", ownedTripIds);
            await deleteActivities.ExecuteNonQueryAsync();
        }

        await using (var deleteMembers = new NpgsqlCommand(deleteMembersSql, conn, transaction))
        {
            deleteMembers.Parameters.AddWithValue("tripIds", ownedTripIds);
            await deleteMembers.ExecuteNonQueryAsync();
        }

        await using (var deleteTrips = new NpgsqlCommand(deleteTripsSql, conn, transaction))
        {
            deleteTrips.Parameters.AddWithValue("tripIds", ownedTripIds);
            await deleteTrips.ExecuteNonQueryAsync();
        }
    }

    const string deleteMembershipsSql = """
        delete from "TripMembers"
        where "UserId" = @userId
        """;

    const string clearAssignmentsSql = """
        update "TripActivities"
        set "AssignedToUserId" = null
        where "AssignedToUserId" = @userId
        """;

    const string deleteUserSql = """
        delete from "Users"
        where "Id" = @userId
        """;

    await using (var deleteMemberships = new NpgsqlCommand(deleteMembershipsSql, conn, transaction))
    {
        deleteMemberships.Parameters.AddWithValue("userId", userId);
        await deleteMemberships.ExecuteNonQueryAsync();
    }

    await using (var clearAssignments = new NpgsqlCommand(clearAssignmentsSql, conn, transaction))
    {
        clearAssignments.Parameters.AddWithValue("userId", userId);
        await clearAssignments.ExecuteNonQueryAsync();
    }

    await using (var deleteUser = new NpgsqlCommand(deleteUserSql, conn, transaction))
    {
        deleteUser.Parameters.AddWithValue("userId", userId);
        var deleted = await deleteUser.ExecuteNonQueryAsync();
        if (deleted != 1)
        {
            await transaction.RollbackAsync();
            Console.Error.WriteLine("User delete did not affect exactly one row.");
            return 1;
        }
    }

    await transaction.CommitAsync();
    Console.WriteLine($"Deleted user {userEmail} ({userName}).");
    return 0;
}
