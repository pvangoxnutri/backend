using System.Security.Claims;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using sidequest.backend.Data;
using sidequest.backend.Dtos;
using sidequest.backend.Models;
using sidequest.backend.Services;

namespace sidequest.backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthController> _logger;
    private readonly ISupabaseStorageService _storage;

    public AuthController(
        AppDbContext db,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<AuthController> logger,
        ISupabaseStorageService storage)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
        _storage = storage;
    }

    [HttpPost("sync")]
    [Authorize]
    public async Task<ActionResult<AuthResponseDto>> Sync([FromBody] SyncAuthUserDto? dto, CancellationToken cancellationToken)
    {
        try
        {
            var user = await GetOrCreateCurrentUserAsync(dto, cancellationToken);
            return Ok(await CreateAuthResponseAsync(user, cancellationToken));
        }
        catch
        {
            return Ok(CreateClaimFallbackResponse(dto));
        }
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<AuthResponseDto>> GetMe(CancellationToken cancellationToken)
    {
        try
        {
            var user = await GetOrCreateCurrentUserAsync(null, cancellationToken);
            return Ok(await CreateAuthResponseAsync(user, cancellationToken));
        }
        catch
        {
            return Ok(CreateClaimFallbackResponse(null));
        }
    }

    [HttpPatch("profile")]
    [Authorize]
    public async Task<ActionResult<AuthResponseDto>> UpdateProfile([FromBody] UpdateProfileDto dto, CancellationToken cancellationToken)
    {
        try
        {
            var user = await GetOrCreateCurrentUserAsync(null, cancellationToken);
            var previousAvatarUrl = user.AvatarUrl;

            if (!string.IsNullOrWhiteSpace(dto.Name)) user.Name = dto.Name.Trim();
            if (dto.AvatarUrl != null) user.AvatarUrl = dto.AvatarUrl;
            if (dto.Bio != null) user.Bio = dto.Bio.Trim().Length > 0 ? dto.Bio.Trim() : null;
            if (dto.HasCompletedOnboarding.HasValue) user.HasCompletedOnboarding = dto.HasCompletedOnboarding.Value;
            if (dto.FoundVia != null) user.FoundVia = dto.FoundVia.Trim().Length > 0 ? dto.FoundVia.Trim() : null;
            if (dto.Purpose != null) user.Purpose = dto.Purpose.Trim().Length > 0 ? dto.Purpose.Trim() : null;
            if (dto.PurposeOtherText != null) user.PurposeOtherText = dto.PurposeOtherText.Trim().Length > 0 ? dto.PurposeOtherText.Trim() : null;

            await _db.SaveChangesAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(previousAvatarUrl) && previousAvatarUrl != user.AvatarUrl)
            {
                await _storage.DeleteByUrlAsync(previousAvatarUrl, cancellationToken);
            }

            return Ok(await CreateAuthResponseAsync(user, cancellationToken));
        }
        catch
        {
            return Ok(CreateClaimFallbackResponse(new SyncAuthUserDto
            {
                Name = dto.Name,
                AvatarUrl = dto.AvatarUrl
            }));
        }
    }

    [HttpDelete("me")]
    [Authorize]
    public async Task<ActionResult> DeleteMyAccount(CancellationToken cancellationToken)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var email = User.FindFirstValue(ClaimTypes.Email)?.Trim().ToLowerInvariant();

        // Collect image URLs that will become orphaned by this deletion.
        // Done BEFORE any DB-row deletion so the joins still work.
        List<string?> imagesToDelete;
        try
        {
            imagesToDelete = await CollectImagesForUserDeletionAsync(userId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enumerate images for user {UserId}; cleanup will be skipped.", userId);
            imagesToDelete = new List<string?>();
        }

        try
        {
            await DeleteLocalUserDataAsync(userId, email, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete local account data for user {UserId}", userId);
            return StatusCode(500, "Could not delete local account data.");
        }

        try
        {
            await DeleteSupabaseAuthUserAsync(userId.ToString(), cancellationToken);
        }
        catch
        {
            return StatusCode(500, "Could not delete auth account.");
        }

        // Best-effort storage cleanup. Failures here are logged inside the service
        // and do not affect the success of the account deletion.
        await _storage.DeleteManyByUrlAsync(imagesToDelete, cancellationToken);

        return NoContent();
    }

    /// <summary>
    /// Collect every image URL that belongs to the user and will become orphaned once
    /// the account (and its owned trips) are deleted. Must run BEFORE the DB cascade.
    /// </summary>
    private async Task<List<string?>> CollectImagesForUserDeletionAsync(Guid userId, CancellationToken cancellationToken)
    {
        var images = new List<string?>();

        var avatar = await _db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.AvatarUrl)
            .FirstOrDefaultAsync(cancellationToken);
        images.Add(avatar);

        var ownedTripIds = await _db.Trips
            .Where(t => t.OwnerId == userId)
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);

        if (ownedTripIds.Count > 0)
        {
            images.AddRange(await _db.Trips
                .Where(t => ownedTripIds.Contains(t.Id))
                .Select(t => t.ImageUrl)
                .ToListAsync(cancellationToken));

            images.AddRange(await _db.TripActivities
                .Where(a => ownedTripIds.Contains(a.TripId))
                .Select(a => a.ImageUrl)
                .ToListAsync(cancellationToken));

            images.AddRange(await _db.ChatMessages
                .Where(m => ownedTripIds.Contains(m.TripId))
                .Select(m => m.ImageUrl)
                .ToListAsync(cancellationToken));
        }

        // Activities authored by this user on trips they don't own are deleted too
        // (see DeleteLocalUserDataAsync) - so their images are orphaned.
        images.AddRange(await _db.TripActivities
            .Where(a => a.OwnerId == userId
                        && (ownedTripIds.Count == 0 || !ownedTripIds.Contains(a.TripId)))
            .Select(a => a.ImageUrl)
            .ToListAsync(cancellationToken));

        return images;
    }

    private async Task DeleteLocalUserDataAsync(Guid userId, string? email, CancellationToken cancellationToken)
    {
        var connectionString = _configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is missing.");

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        var ownedTripIds = new List<Guid>();
        await using (var ownedTrips = new NpgsqlCommand("""select "Id" from "Trips" where "OwnerId" = @userId""", conn, tx))
        {
            ownedTrips.Parameters.AddWithValue("userId", userId);
            await using var reader = await ownedTrips.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                ownedTripIds.Add(reader.GetGuid(0));
            }
        }

        if (ownedTripIds.Count > 0)
        {
            await using (var deleteActivities = new NpgsqlCommand("""delete from "TripActivities" where "TripId" = any(@tripIds)""", conn, tx))
            {
                deleteActivities.Parameters.AddWithValue("tripIds", ownedTripIds);
                await deleteActivities.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var deleteInvites = new NpgsqlCommand("""delete from "TripInvites" where "TripId" = any(@tripIds)""", conn, tx))
            {
                deleteInvites.Parameters.AddWithValue("tripIds", ownedTripIds);
                await deleteInvites.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var deleteMembers = new NpgsqlCommand("""delete from "TripMembers" where "TripId" = any(@tripIds)""", conn, tx))
            {
                deleteMembers.Parameters.AddWithValue("tripIds", ownedTripIds);
                await deleteMembers.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var deleteTrips = new NpgsqlCommand("""delete from "Trips" where "Id" = any(@tripIds)""", conn, tx))
            {
                deleteTrips.Parameters.AddWithValue("tripIds", ownedTripIds);
                await deleteTrips.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using (var deleteMyActivities = new NpgsqlCommand("""delete from "TripActivities" where "OwnerId" = @userId""", conn, tx))
        {
            deleteMyActivities.Parameters.AddWithValue("userId", userId);
            await deleteMyActivities.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteMyInvites = new NpgsqlCommand("""delete from "TripInvites" where "InvitedByUserId" = @userId""", conn, tx))
        {
            deleteMyInvites.Parameters.AddWithValue("userId", userId);
            await deleteMyInvites.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(email))
        {
            await using var deleteInvitesForEmail = new NpgsqlCommand("""delete from "TripInvites" where lower("Email") = @email""", conn, tx);
            deleteInvitesForEmail.Parameters.AddWithValue("email", email);
            await deleteInvitesForEmail.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteMemberships = new NpgsqlCommand("""delete from "TripMembers" where "UserId" = @userId""", conn, tx))
        {
            deleteMemberships.Parameters.AddWithValue("userId", userId);
            await deleteMemberships.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var clearAssignments = new NpgsqlCommand("""update "TripActivities" set "AssignedToUserId" = null where "AssignedToUserId" = @userId""", conn, tx))
        {
            clearAssignments.Parameters.AddWithValue("userId", userId);
            await clearAssignments.ExecuteNonQueryAsync(cancellationToken);
        }

        // Delete activity comments by this user
        await using (var deleteComments = new NpgsqlCommand("""delete from "ActivityComments" where "UserId" = @userId""", conn, tx))
        {
            deleteComments.Parameters.AddWithValue("userId", userId);
            await deleteComments.ExecuteNonQueryAsync(cancellationToken);
        }

        // Delete settlements involving this user
        await using (var deleteSettlements = new NpgsqlCommand("""delete from "Settlements" where "FromUserId" = @userId or "ToUserId" = @userId""", conn, tx))
        {
            deleteSettlements.Parameters.AddWithValue("userId", userId);
            await deleteSettlements.ExecuteNonQueryAsync(cancellationToken);
        }

        // Delete expenses created by this user on trips they didn't own
        // (cascade removes the payers/participants for those expenses)
        await using (var deleteCreatedExpenses = new NpgsqlCommand("""delete from "Expenses" where "CreatedByUserId" = @userId""", conn, tx))
        {
            deleteCreatedExpenses.Parameters.AddWithValue("userId", userId);
            await deleteCreatedExpenses.ExecuteNonQueryAsync(cancellationToken);
        }

        // Remove this user from payer/participant lists on other people's expenses
        await using (var deleteExpensePayers = new NpgsqlCommand("""delete from "ExpensePayers" where "UserId" = @userId""", conn, tx))
        {
            deleteExpensePayers.Parameters.AddWithValue("userId", userId);
            await deleteExpensePayers.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteExpenseParticipants = new NpgsqlCommand("""delete from "ExpenseParticipants" where "UserId" = @userId""", conn, tx))
        {
            deleteExpenseParticipants.Parameters.AddWithValue("userId", userId);
            await deleteExpenseParticipants.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteUser = new NpgsqlCommand("""delete from "Users" where "Id" = @userId""", conn, tx))
        {
            deleteUser.Parameters.AddWithValue("userId", userId);
            await deleteUser.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    private async Task DeleteSupabaseAuthUserAsync(string supabaseUserId, CancellationToken cancellationToken)
    {
        var supabaseUrl = _configuration["Supabase:Url"]?.TrimEnd('/');
        var serviceRoleKey =
            _configuration["Supabase:ServiceRoleKey"]
            ?? Environment.GetEnvironmentVariable("SUPABASE_SERVICE_ROLE_KEY");

        if (string.IsNullOrWhiteSpace(supabaseUrl))
            throw new InvalidOperationException("Supabase:Url is missing.");

        if (string.IsNullOrWhiteSpace(serviceRoleKey))
            throw new InvalidOperationException("Supabase service role key is missing. Set Supabase:ServiceRoleKey or SUPABASE_SERVICE_ROLE_KEY.");

        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"{supabaseUrl}/auth/v1/admin/users/{Uri.EscapeDataString(supabaseUserId)}");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceRoleKey);
        request.Headers.Add("apikey", serviceRoleKey);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Already deleted from Supabase auth.
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Supabase delete failed ({(int)response.StatusCode}): {body}");
        }
    }

    private async Task<User> GetOrCreateCurrentUserAsync(SyncAuthUserDto? dto, CancellationToken cancellationToken)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var email = User.FindFirstValue(ClaimTypes.Email)?.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(email))
            throw new InvalidOperationException("Authenticated Supabase user is missing an email claim.");

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user == null)
        {
            user = new User
            {
                Id = userId,
                Email = email,
                Name = dto?.Name?.Trim() ?? User.FindFirstValue(ClaimTypes.Name) ?? email,
                AvatarUrl = dto?.AvatarUrl,
                Role = "user"
            };

            _db.Users.Add(user);
        }
        else
        {
            user.Email = email;

            if (!string.IsNullOrWhiteSpace(dto?.Name))
                user.Name = dto.Name.Trim();

            if (dto?.AvatarUrl != null)
                user.AvatarUrl = dto.AvatarUrl;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return user;
    }

    private async Task<AuthResponseDto> CreateAuthResponseAsync(User user, CancellationToken cancellationToken)
    {
        return new AuthResponseDto
        {
            Token = string.Empty,
            Id = user.Id,
            Name = user.Name,
            Email = user.Email,
            EmailVerified = true,
            AvatarUrl = user.AvatarUrl,
            Bio = user.Bio,
            HasCompletedOnboarding = user.HasCompletedOnboarding,
            Role = user.Role,
        };
    }

    private AuthResponseDto CreateClaimFallbackResponse(SyncAuthUserDto? dto)
    {
        var idRaw = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        Guid.TryParse(idRaw, out var id);

        var email = User.FindFirstValue(ClaimTypes.Email)?.Trim().ToLowerInvariant() ?? string.Empty;
        var claimName = User.FindFirstValue(ClaimTypes.Name);
        var name = !string.IsNullOrWhiteSpace(dto?.Name)
            ? dto!.Name!.Trim()
            : !string.IsNullOrWhiteSpace(claimName)
                ? claimName!
                : (!string.IsNullOrWhiteSpace(email) ? email : "User");

        return new AuthResponseDto
        {
            Token = string.Empty,
            Id = id,
            Name = name,
            Email = email,
            EmailVerified = true,
            AvatarUrl = dto?.AvatarUrl,
            Role = User.FindFirstValue(ClaimTypes.Role) ?? "user"
        };
    }
}
