using Npgsql;
using System.Text.Json;

var projectRoot = ResolveProjectRoot();
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

    case "grant-all-trips-owner":
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Missing email for grant-all-trips-owner.");
            PrintUsage();
            return 1;
        }

        return await GrantAllTripsOwnerAsync(conn, args[1]);

    case "list-user-trips":
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Missing email for list-user-trips.");
            PrintUsage();
            return 1;
        }

        return await ListUserTripsAsync(conn, args[1]);

    case "stats":
        return await PrintStatsAsync(conn);

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
    Console.WriteLine("  dotnet run --project Tools/DbAdmin -- stats");
    Console.WriteLine("  dotnet run --project Tools/DbAdmin -- list-user-trips <email>");
    Console.WriteLine("  dotnet run --project Tools/DbAdmin -- grant-all-trips-owner <email>");
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

static string ResolveProjectRoot()
{
    var cwd = Directory.GetCurrentDirectory();
    if (File.Exists(Path.Combine(cwd, "appsettings.Development.json")) ||
        File.Exists(Path.Combine(cwd, "appsettings.json")))
    {
        return cwd;
    }

    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "appsettings.Development.json")) ||
            File.Exists(Path.Combine(dir.FullName, "appsettings.json")))
        {
            return dir.FullName;
        }

        dir = dir.Parent;
    }

    return cwd;
}

static async Task ListUsersAsync(NpgsqlConnection conn)
{
    const string sql = """
        select "Id", "Email", "Name", "CreatedAt"
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
        var createdAt = reader.GetDateTime(3);
        Console.WriteLine($"{id}\t{email}\t{name}\t{createdAt:O}");
    }
}

static async Task<int> GrantAllTripsOwnerAsync(NpgsqlConnection conn, string email)
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

    await using var tx = await conn.BeginTransactionAsync();

    const string missingTripsSql = """
        select t."Id"
        from "Trips" t
        where not exists (
            select 1 from "TripMembers" tm
            where tm."TripId" = t."Id" and tm."UserId" = @userId
        )
        """;

    const string insertMembershipSql = """
        insert into "TripMembers" ("Id", "TripId", "UserId", "IsOwner", "JoinedAt")
        values (@id, @tripId, @userId, true, now())
        """;

    const string promoteExistingMembershipsSql = """
        update "TripMembers"
        set "IsOwner" = true
        where "UserId" = @userId and "IsOwner" = false
        """;

    int inserted = 0;
    int promoted;

    var missingTripIds = new List<Guid>();
    await using (var missingTrips = new NpgsqlCommand(missingTripsSql, conn, tx))
    {
        missingTrips.Parameters.AddWithValue("userId", userId);
        await using var reader = await missingTrips.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            missingTripIds.Add(reader.GetGuid(0));
        }
    }

    foreach (var tripId in missingTripIds)
    {
        await using var insert = new NpgsqlCommand(insertMembershipSql, conn, tx);
        insert.Parameters.AddWithValue("id", Guid.NewGuid());
        insert.Parameters.AddWithValue("tripId", tripId);
        insert.Parameters.AddWithValue("userId", userId);
        inserted += await insert.ExecuteNonQueryAsync();
    }

    await using (var promote = new NpgsqlCommand(promoteExistingMembershipsSql, conn, tx))
    {
        promote.Parameters.AddWithValue("userId", userId);
        promoted = await promote.ExecuteNonQueryAsync();
    }

    await tx.CommitAsync();

    Console.WriteLine($"Updated user {userEmail} ({userName}).");
    Console.WriteLine($"Added memberships: {inserted}");
    Console.WriteLine($"Promoted to owner: {promoted}");
    return 0;
}

static async Task<int> PrintStatsAsync(NpgsqlConnection conn)
{
    const string sql = """
        select
            (select count(*) from "Users") as users_count,
            (select count(*) from "Trips") as trips_count,
            (select count(*) from "TripMembers") as members_count,
            (select count(*) from "TripActivities") as activities_count
        """;

    await using var cmd = new NpgsqlCommand(sql, conn);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
    {
        Console.WriteLine("No stats.");
        return 1;
    }

    Console.WriteLine($"Users: {reader.GetInt64(0)}");
    Console.WriteLine($"Trips: {reader.GetInt64(1)}");
    Console.WriteLine($"TripMembers: {reader.GetInt64(2)}");
    Console.WriteLine($"TripActivities: {reader.GetInt64(3)}");
    return 0;
}

static async Task<int> ListUserTripsAsync(NpgsqlConnection conn, string email)
{
    var normalizedEmail = email.Trim().ToLowerInvariant();

    const string sql = """
        select
            u."Id" as "UserId",
            u."Email",
            t."Id" as "TripId",
            t."Title",
            tm."IsOwner"
        from "Users" u
        left join "TripMembers" tm on tm."UserId" = u."Id"
        left join "Trips" t on t."Id" = tm."TripId"
        where lower(u."Email") = @email
        order by t."CreatedAt" desc nulls last
        """;

    await using var cmd = new NpgsqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("email", normalizedEmail);

    await using var reader = await cmd.ExecuteReaderAsync();

    var foundUser = false;
    var count = 0;
    while (await reader.ReadAsync())
    {
        foundUser = true;
        var userId = reader.GetGuid(0);
        var userEmail = reader.GetString(1);
        if (count == 0)
        {
            Console.WriteLine($"User: {userEmail} ({userId})");
        }

        if (reader.IsDBNull(2))
        {
            continue;
        }

        count++;
        var tripId = reader.GetGuid(2);
        var title = reader.IsDBNull(3) ? "(untitled)" : reader.GetString(3);
        var isOwner = !reader.IsDBNull(4) && reader.GetBoolean(4);
        Console.WriteLine($"{count}. {title} [{tripId}] owner={isOwner}");
    }

    if (!foundUser)
    {
        Console.WriteLine($"No user found for {normalizedEmail}");
        return 1;
    }

    Console.WriteLine($"Total trips for user: {count}");
    return 0;
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
