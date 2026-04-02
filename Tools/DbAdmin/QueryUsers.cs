using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Npgsql;

var projectRoot = args.Length > 0 ? args[0] : throw new InvalidOperationException("Expected project root path.");
var mode = args.Length > 1 ? args[1] : "list";
var email = args.Length > 2 ? args[2] : string.Empty;

var connectionString = ReadConnectionString(projectRoot)
    ?? throw new InvalidOperationException("DefaultConnection was not found.");

await using var conn = new NpgsqlConnection(connectionString);
await conn.OpenAsync();

if (string.Equals(mode, "list", StringComparison.OrdinalIgnoreCase))
{
    const string listSql = """
        select "Id", "Email", "Name", "CreatedAt"
        from "Users"
        order by "CreatedAt" desc
        """;

    await using var cmd = new NpgsqlCommand(listSql, conn);
    await using var reader = await cmd.ExecuteReaderAsync();

    while (await reader.ReadAsync())
    {
        Console.WriteLine($"{reader.GetGuid(0)}\t{reader.GetString(1)}\t{reader.GetString(2)}\t{reader.GetDateTime(3):O}");
    }

    return;
}

if (string.IsNullOrWhiteSpace(email))
{
    throw new InvalidOperationException("Expected email for delete mode.");
}

var normalizedEmail = email.Trim().ToLowerInvariant();
Guid userId;
string userEmail;
string userName;

const string lookupSql = """
    select "Id", "Email", "Name"
    from "Users"
    where lower("Email") = @email
    """;

await using (var lookup = new NpgsqlCommand(lookupSql, conn))
{
    lookup.Parameters.AddWithValue("email", normalizedEmail);
    await using var reader = await lookup.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
    {
        Console.Error.WriteLine($"No user found for {normalizedEmail}.");
        return;
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
    await using (var deleteActivities = new NpgsqlCommand("""delete from "TripActivities" where "TripId" = any(@tripIds)""", conn, transaction))
    {
        deleteActivities.Parameters.AddWithValue("tripIds", ownedTripIds);
        await deleteActivities.ExecuteNonQueryAsync();
    }

    await using (var deleteMembers = new NpgsqlCommand("""delete from "TripMembers" where "TripId" = any(@tripIds)""", conn, transaction))
    {
        deleteMembers.Parameters.AddWithValue("tripIds", ownedTripIds);
        await deleteMembers.ExecuteNonQueryAsync();
    }

    await using (var deleteTrips = new NpgsqlCommand("""delete from "Trips" where "Id" = any(@tripIds)""", conn, transaction))
    {
        deleteTrips.Parameters.AddWithValue("tripIds", ownedTripIds);
        await deleteTrips.ExecuteNonQueryAsync();
    }
}

await using (var deleteMemberships = new NpgsqlCommand("""delete from "TripMembers" where "UserId" = @userId""", conn, transaction))
{
    deleteMemberships.Parameters.AddWithValue("userId", userId);
    await deleteMemberships.ExecuteNonQueryAsync();
}

await using (var clearAssignments = new NpgsqlCommand("""update "TripActivities" set "AssignedToUserId" = null where "AssignedToUserId" = @userId""", conn, transaction))
{
    clearAssignments.Parameters.AddWithValue("userId", userId);
    await clearAssignments.ExecuteNonQueryAsync();
}

await using (var deleteUser = new NpgsqlCommand("""delete from "Users" where "Id" = @userId""", conn, transaction))
{
    deleteUser.Parameters.AddWithValue("userId", userId);
    var deleted = await deleteUser.ExecuteNonQueryAsync();
    if (deleted != 1)
    {
        throw new InvalidOperationException("User delete did not affect exactly one row.");
    }
}

await transaction.CommitAsync();
Console.WriteLine($"Deleted user {userEmail} ({userName}).");

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
