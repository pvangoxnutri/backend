using System.Diagnostics;
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
        var total = Stopwatch.StartNew();

        // Cap the DB work at 3s. The JWT-claim fallback gives the iPhone
        // enough to render Home; a slow Npgsql connection-pool wait would
        // otherwise stack two 15s waits (lookup + SaveChanges) and produce
        // the ~32s hangs observed in Railway logs.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(3));

        try
        {
            var phase = Stopwatch.StartNew();
            var user = await GetOrCreateCurrentUserAsync(dto, linkedCts.Token);
            _logger.LogInformation("[TIMING] POST /api/auth/sync phase=getOrCreateUser elapsedMs={Elapsed}", phase.ElapsedMilliseconds);

            phase.Restart();
            var response = await CreateAuthResponseAsync(user, linkedCts.Token);
            _logger.LogInformation("[TIMING] POST /api/auth/sync phase=createResponse elapsedMs={Elapsed}", phase.ElapsedMilliseconds);

            _logger.LogInformation("[TIMING] POST /api/auth/sync total elapsedMs={Elapsed}", total.ElapsedMilliseconds);
            return Ok(response);
        }
        catch (Exception ex)
        {
            var exType = ex.GetType().Name;
            var inner = ex.InnerException is not null ? $" inner={ex.InnerException.GetType().Name}: {ex.InnerException.Message}" : "";
            var deadlineHit = linkedCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            _logger.LogWarning(ex, "[TIMING] POST /api/auth/sync FAILED type={Type} msg={Msg}{Inner} deadlineHit={Deadline} elapsedMs={Elapsed} (returning claim fallback)",
                exType, ex.Message, inner, deadlineHit, total.ElapsedMilliseconds);
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
        var total = Stopwatch.StartNew();
        try
        {
            var phase = Stopwatch.StartNew();
            var user = await GetOrCreateCurrentUserAsync(null, cancellationToken);
            _logger.LogInformation("[TIMING] PATCH /api/auth/profile phase=getOrCreateUser elapsedMs={Elapsed}", phase.ElapsedMilliseconds);
            var previousAvatarUrl = user.AvatarUrl;

            if (!string.IsNullOrWhiteSpace(dto.Name)) user.Name = dto.Name.Trim();
            if (dto.AvatarUrl != null) user.AvatarUrl = dto.AvatarUrl;
            if (dto.Bio != null) user.Bio = dto.Bio.Trim().Length > 0 ? dto.Bio.Trim() : null;
            if (dto.HasCompletedOnboarding.HasValue) user.HasCompletedOnboarding = dto.HasCompletedOnboarding.Value;
            if (dto.FoundVia != null) user.FoundVia = dto.FoundVia.Trim().Length > 0 ? dto.FoundVia.Trim() : null;
            if (dto.Purpose != null) user.Purpose = dto.Purpose.Trim().Length > 0 ? dto.Purpose.Trim() : null;
            if (dto.PurposeOtherText != null) user.PurposeOtherText = dto.PurposeOtherText.Trim().Length > 0 ? dto.PurposeOtherText.Trim() : null;
            if (dto.Language != null) user.Language = NormalizeLanguage(dto.Language);

            phase.Restart();
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("[TIMING] PATCH /api/auth/profile phase=saveChanges elapsedMs={Elapsed}", phase.ElapsedMilliseconds);

            if (!string.IsNullOrWhiteSpace(previousAvatarUrl) && previousAvatarUrl != user.AvatarUrl)
            {
                phase.Restart();
                await _storage.DeleteByUrlAsync(previousAvatarUrl, cancellationToken);
                _logger.LogInformation("[TIMING] PATCH /api/auth/profile phase=deleteOldAvatar elapsedMs={Elapsed}", phase.ElapsedMilliseconds);
            }

            phase.Restart();
            var response = await CreateAuthResponseAsync(user, cancellationToken);
            _logger.LogInformation("[TIMING] PATCH /api/auth/profile phase=createResponse elapsedMs={Elapsed}", phase.ElapsedMilliseconds);
            _logger.LogInformation("[TIMING] PATCH /api/auth/profile total elapsedMs={Elapsed}", total.ElapsedMilliseconds);
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[TIMING] PATCH /api/auth/profile FAILED, returning claim fallback elapsedMs={Elapsed}", total.ElapsedMilliseconds);
            return Ok(CreateClaimFallbackResponse(new SyncAuthUserDto
            {
                Name = dto.Name,
                AvatarUrl = dto.AvatarUrl
            }));
        }
    }

    // ── GET /api/auth/me/export ────────────────────────────────────────────────
    // GDPR data export: returns a structured JSON snapshot of everything we
    // store where the current user is the data subject. Read-only; no DB writes.

    [HttpGet("me/export")]
    [Authorize]
    public async Task<ActionResult<UserExportDto>> ExportMyData(CancellationToken cancellationToken)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var email = User.FindFirstValue(ClaimTypes.Email)?.Trim().ToLowerInvariant() ?? string.Empty;

        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user == null) return NotFound();

        // Trip membership split: trips I own vs trips I am only a member of
        var memberRows = await _db.TripMembers.AsNoTracking()
            .Where(tm => tm.UserId == userId)
            .ToListAsync(cancellationToken);
        var memberTripIds = memberRows.Select(tm => tm.TripId).ToList();

        var ownedTrips = await _db.Trips.AsNoTracking()
            .Where(t => t.OwnerId == userId)
            .ToListAsync(cancellationToken);
        var ownedTripIds = ownedTrips.Select(t => t.Id).ToHashSet();

        var memberOnlyTripIds = memberTripIds.Where(id => !ownedTripIds.Contains(id)).ToList();
        var memberOnlyTrips = await _db.Trips.AsNoTracking()
            .Where(t => memberOnlyTripIds.Contains(t.Id))
            .ToListAsync(cancellationToken);

        // Activities the user authored (anywhere)
        var myActivities = await _db.TripActivities.AsNoTracking()
            .Where(a => a.OwnerId == userId)
            .ToListAsync(cancellationToken);

        // Comments the user wrote
        var myComments = await _db.ActivityComments.AsNoTracking()
            .Where(c => c.UserId == userId)
            .ToListAsync(cancellationToken);

        // Chat messages the user sent (anonymous system messages skipped via UserId check)
        var myChat = await _db.ChatMessages.AsNoTracking()
            .Where(m => m.UserId == userId)
            .ToListAsync(cancellationToken);

        // Expenses the user created, paid for, or participated in
        var expensesCreated = await _db.Expenses.AsNoTracking()
            .Where(e => e.CreatedByUserId == userId)
            .ToListAsync(cancellationToken);

        var myPayerRows = await _db.ExpensePayers.AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => new { p.ExpenseId, p.Expense.TripId, p.Amount })
            .ToListAsync(cancellationToken);

        var myParticipantRows = await _db.ExpenseParticipants.AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => new { p.ExpenseId, p.Expense.TripId, p.Amount })
            .ToListAsync(cancellationToken);

        var settlements = await _db.Settlements.AsNoTracking()
            .Where(s => s.FromUserId == userId || s.ToUserId == userId)
            .ToListAsync(cancellationToken);

        // Invites I sent
        var invitesSent = await _db.TripInvites.AsNoTracking()
            .Where(i => i.InvitedByUserId == userId)
            .ToListAsync(cancellationToken);

        // Invites for my email
        var invitesReceived = string.IsNullOrWhiteSpace(email)
            ? new List<Models.TripInvite>()
            : await _db.TripInvites.AsNoTracking()
                .Where(i => i.Email == email)
                .ToListAsync(cancellationToken);

        var feedback = await _db.Feedbacks.AsNoTracking()
            .Where(f => f.UserId == userId.ToString())
            .ToListAsync(cancellationToken);

        var events = await _db.TripEvents.AsNoTracking()
            .Where(e => e.ActorId == userId)
            .ToListAsync(cancellationToken);

        // Flat list of every image URL the user is responsible for
        var imageUrls = new List<string?>
        {
            user.AvatarUrl,
        };
        imageUrls.AddRange(ownedTrips.Select(t => t.ImageUrl));
        imageUrls.AddRange(myActivities.Select(a => a.ImageUrl));
        imageUrls.AddRange(myChat.Select(m => m.ImageUrl));

        var memberMap = memberRows.ToDictionary(tm => tm.TripId, tm => tm);

        var export = new UserExportDto
        {
            ExportedAt = DateTime.UtcNow,
            SchemaVersion = "1.0",
            User = new ExportedUserDto
            {
                Id = user.Id,
                Email = user.Email,
                Name = user.Name,
                AvatarUrl = user.AvatarUrl,
                Bio = user.Bio,
                Role = user.Role,
                HasCompletedOnboarding = user.HasCompletedOnboarding,
                FoundVia = user.FoundVia,
                Purpose = user.Purpose,
                PurposeOtherText = user.PurposeOtherText,
                CreatedAt = user.CreatedAt,
            },
            OwnedTrips = ownedTrips.Select(t => new ExportedTripDto
            {
                Id = t.Id,
                Title = t.Title,
                Description = t.Description,
                Destination = t.Destination,
                StartDate = t.StartDate,
                EndDate = t.EndDate,
                Visibility = t.Visibility,
                RevealAt = t.RevealAt,
                Teaser = t.Teaser,
                ImageUrl = t.ImageUrl,
                SpotifyUrl = t.SpotifyUrl,
                InviteCode = t.InviteCode,
                Status = t.Status,
                ShareCode = t.ShareCode,
                SharedAt = t.SharedAt,
                OwnerId = t.OwnerId,
                CreatedAt = t.CreatedAt,
            }).ToList(),
            MemberTrips = memberOnlyTrips.Select(t => new ExportedTripDto
            {
                Id = t.Id,
                Title = t.Title,
                Description = t.Description,
                Destination = t.Destination,
                StartDate = t.StartDate,
                EndDate = t.EndDate,
                Visibility = t.Visibility,
                RevealAt = t.RevealAt,
                Teaser = t.Teaser,
                ImageUrl = t.ImageUrl,
                SpotifyUrl = t.SpotifyUrl,
                InviteCode = t.InviteCode,
                Status = t.Status,
                ShareCode = t.ShareCode,
                SharedAt = t.SharedAt,
                OwnerId = t.OwnerId,
                CreatedAt = t.CreatedAt,
                JoinedAt = memberMap.TryGetValue(t.Id, out var m) ? m.JoinedAt : null,
                IsOwnerMembership = memberMap.TryGetValue(t.Id, out var m2) ? m2.IsOwner : null,
            }).ToList(),
            Activities = myActivities.Select(a => new ExportedActivityDto
            {
                Id = a.Id,
                TripId = a.TripId,
                Date = a.Date,
                Title = a.Title,
                Description = a.Description,
                Time = a.Time,
                Category = a.Category,
                ImageUrl = a.ImageUrl,
                SpotifyUrl = a.SpotifyUrl,
                Visibility = a.Visibility,
                RevealAt = a.RevealAt,
                Teaser = a.Teaser,
                TeaserOffsetMinutes = a.TeaserOffsetMinutes,
                IsHidden = a.IsHidden,
                AssignedToUserId = a.AssignedToUserId,
                CreatedAt = a.CreatedAt,
            }).ToList(),
            ActivityComments = myComments.Select(c => new ExportedCommentDto
            {
                Id = c.Id,
                ActivityId = c.ActivityId,
                Text = c.Text,
                CreatedAt = c.CreatedAt,
            }).ToList(),
            ChatMessages = myChat.Select(m => new ExportedChatMessageDto
            {
                Id = m.Id,
                TripId = m.TripId,
                Text = m.Text,
                ImageUrl = m.ImageUrl,
                IsSystem = m.IsSystem,
                CreatedAt = m.CreatedAt,
            }).ToList(),
            Expenses = new ExportedExpensesDto
            {
                Created = expensesCreated.Select(e => new ExportedExpenseDto
                {
                    Id = e.Id,
                    TripId = e.TripId,
                    Description = e.Description,
                    TotalAmount = e.TotalAmount,
                    Date = e.Date,
                    SplitMode = e.SplitMode,
                    Currency = e.Currency,
                    CreatedAt = e.CreatedAt,
                }).ToList(),
                PaidBy = myPayerRows.Select(p => new ExportedExpenseShareDto
                {
                    ExpenseId = p.ExpenseId,
                    TripId = p.TripId,
                    Amount = p.Amount,
                }).ToList(),
                ParticipatedIn = myParticipantRows.Select(p => new ExportedExpenseShareDto
                {
                    ExpenseId = p.ExpenseId,
                    TripId = p.TripId,
                    Amount = p.Amount,
                }).ToList(),
            },
            Settlements = settlements.Select(s => new ExportedSettlementDto
            {
                Id = s.Id,
                TripId = s.TripId,
                FromUserId = s.FromUserId,
                ToUserId = s.ToUserId,
                Amount = s.Amount,
                Note = s.Note,
                CreatedAt = s.CreatedAt,
            }).ToList(),
            InvitesSent = invitesSent.Select(i => new ExportedInviteDto
            {
                Id = i.Id,
                TripId = i.TripId,
                Email = i.Email,
                InvitedByUserId = i.InvitedByUserId,
                Status = i.Status,
                CreatedAt = i.CreatedAt,
            }).ToList(),
            InvitesReceived = invitesReceived.Select(i => new ExportedInviteDto
            {
                Id = i.Id,
                TripId = i.TripId,
                Email = i.Email,
                InvitedByUserId = i.InvitedByUserId,
                Status = i.Status,
                CreatedAt = i.CreatedAt,
            }).ToList(),
            Feedback = feedback.Select(f => new ExportedFeedbackDto
            {
                Id = f.Id,
                Type = f.Type,
                Message = f.Message,
                CreatedAt = f.CreatedAt,
            }).ToList(),
            TripEvents = events.Select(e => new ExportedTripEventDto
            {
                Id = e.Id,
                TripId = e.TripId,
                Type = e.Type,
                CreatedAt = e.CreatedAt,
            }).ToList(),
            UploadedImageUrls = imageUrls
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Select(u => u!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };

        return Ok(export);
    }

    [HttpDelete("me")]
    [Authorize]
    public async Task<ActionResult> DeleteMyAccount(CancellationToken cancellationToken)
    {
        var total = Stopwatch.StartNew();
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var email = User.FindFirstValue(ClaimTypes.Email)?.Trim().ToLowerInvariant();
        _logger.LogInformation("[TIMING] DELETE /api/auth/me start userId={UserId}", userId);

        // Collect image URLs that will become orphaned by this deletion.
        // Done BEFORE any DB-row deletion so the joins still work.
        List<string?> imagesToDelete;
        var phase = Stopwatch.StartNew();
        try
        {
            imagesToDelete = await CollectImagesForUserDeletionAsync(userId, cancellationToken);
            _logger.LogInformation("[TIMING] DELETE /api/auth/me phase=collectImages imageCount={Count} elapsedMs={Elapsed}", imagesToDelete.Count, phase.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[TIMING] DELETE /api/auth/me phase=collectImages FAILED elapsedMs={Elapsed} (cleanup skipped)", phase.ElapsedMilliseconds);
            imagesToDelete = new List<string?>();
        }

        phase.Restart();
        try
        {
            await DeleteLocalUserDataAsync(userId, email, cancellationToken);
            _logger.LogInformation("[TIMING] DELETE /api/auth/me phase=deleteLocal elapsedMs={Elapsed}", phase.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[TIMING] DELETE /api/auth/me phase=deleteLocal FAILED elapsedMs={Elapsed}", phase.ElapsedMilliseconds);
            return StatusCode(500, "Could not delete local account data.");
        }

        phase.Restart();
        try
        {
            await DeleteSupabaseAuthUserAsync(userId.ToString(), cancellationToken);
            _logger.LogInformation("[TIMING] DELETE /api/auth/me phase=deleteSupabaseAuth elapsedMs={Elapsed}", phase.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[TIMING] DELETE /api/auth/me phase=deleteSupabaseAuth FAILED elapsedMs={Elapsed}", phase.ElapsedMilliseconds);
            return StatusCode(500, "Could not delete auth account.");
        }

        // Best-effort storage cleanup. Failures here are logged inside the service
        // and do not affect the success of the account deletion.
        phase.Restart();
        await _storage.DeleteManyByUrlAsync(imagesToDelete, cancellationToken);
        _logger.LogInformation("[TIMING] DELETE /api/auth/me phase=deleteStorage imageCount={Count} elapsedMs={Elapsed}", imagesToDelete.Count, phase.ElapsedMilliseconds);

        _logger.LogInformation("[TIMING] DELETE /api/auth/me total elapsedMs={Elapsed}", total.ElapsedMilliseconds);
        return NoContent();
    }

    /// <summary>
    /// Collect every image URL that belongs to the user and will become orphaned once
    /// the account (and its owned trips) are deleted. Must run BEFORE the DB cascade.
    /// </summary>
    private async Task<List<string?>> CollectImagesForUserDeletionAsync(Guid userId, CancellationToken cancellationToken)
    {
        var images = new List<string?>();
        var sw = Stopwatch.StartNew();

        var avatar = await _db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.AvatarUrl)
            .FirstOrDefaultAsync(cancellationToken);
        images.Add(avatar);
        _logger.LogInformation("[TIMING] collectImages step=avatar elapsedMs={Elapsed}", sw.ElapsedMilliseconds);

        sw.Restart();
        var ownedTripIds = await _db.Trips
            .Where(t => t.OwnerId == userId)
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);
        _logger.LogInformation("[TIMING] collectImages step=ownedTripIds count={Count} elapsedMs={Elapsed}", ownedTripIds.Count, sw.ElapsedMilliseconds);

        if (ownedTripIds.Count > 0)
        {
            sw.Restart();
            images.AddRange(await _db.Trips
                .Where(t => ownedTripIds.Contains(t.Id))
                .Select(t => t.ImageUrl)
                .ToListAsync(cancellationToken));
            _logger.LogInformation("[TIMING] collectImages step=tripImages elapsedMs={Elapsed}", sw.ElapsedMilliseconds);

            sw.Restart();
            images.AddRange(await _db.TripActivities
                .Where(a => ownedTripIds.Contains(a.TripId))
                .Select(a => a.ImageUrl)
                .ToListAsync(cancellationToken));
            _logger.LogInformation("[TIMING] collectImages step=ownedActivityImages elapsedMs={Elapsed}", sw.ElapsedMilliseconds);

            sw.Restart();
            images.AddRange(await _db.ChatMessages
                .Where(m => ownedTripIds.Contains(m.TripId))
                .Select(m => m.ImageUrl)
                .ToListAsync(cancellationToken));
            _logger.LogInformation("[TIMING] collectImages step=chatImages elapsedMs={Elapsed}", sw.ElapsedMilliseconds);
        }

        // Activities authored by this user on trips they don't own are deleted too
        // (see DeleteLocalUserDataAsync) - so their images are orphaned.
        sw.Restart();
        images.AddRange(await _db.TripActivities
            .Where(a => a.OwnerId == userId
                        && (ownedTripIds.Count == 0 || !ownedTripIds.Contains(a.TripId)))
            .Select(a => a.ImageUrl)
            .ToListAsync(cancellationToken));
        _logger.LogInformation("[TIMING] collectImages step=authoredActivityImages elapsedMs={Elapsed}", sw.ElapsedMilliseconds);

        return images;
    }

    private async Task DeleteLocalUserDataAsync(Guid userId, string? email, CancellationToken cancellationToken)
    {
        var connectionString = _configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is missing.");

        var sw = Stopwatch.StartNew();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        _logger.LogInformation("[TIMING] deleteLocal step=openConnection elapsedMs={Elapsed}", sw.ElapsedMilliseconds);

        sw.Restart();
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);
        _logger.LogInformation("[TIMING] deleteLocal step=beginTx elapsedMs={Elapsed}", sw.ElapsedMilliseconds);

        sw.Restart();
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
        _logger.LogInformation("[TIMING] deleteLocal step=findOwnedTripIds count={Count} elapsedMs={Elapsed}", ownedTripIds.Count, sw.ElapsedMilliseconds);

        if (ownedTripIds.Count > 0)
        {
            sw.Restart();
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
            _logger.LogInformation("[TIMING] deleteLocal step=ownedTripCleanup(4stmts) elapsedMs={Elapsed}", sw.ElapsedMilliseconds);
        }

        sw.Restart();
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
        _logger.LogInformation("[TIMING] deleteLocal step=userScopedCleanup(6stmts) elapsedMs={Elapsed}", sw.ElapsedMilliseconds);

        sw.Restart();
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
        _logger.LogInformation("[TIMING] deleteLocal step=expenseCleanup(4stmts) elapsedMs={Elapsed}", sw.ElapsedMilliseconds);

        sw.Restart();
        await using (var deleteUser = new NpgsqlCommand("""delete from "Users" where "Id" = @userId""", conn, tx))
        {
            deleteUser.Parameters.AddWithValue("userId", userId);
            await deleteUser.ExecuteNonQueryAsync(cancellationToken);
        }
        _logger.LogInformation("[TIMING] deleteLocal step=deleteUserRow elapsedMs={Elapsed}", sw.ElapsedMilliseconds);

        sw.Restart();
        await tx.CommitAsync(cancellationToken);
        _logger.LogInformation("[TIMING] deleteLocal step=commit elapsedMs={Elapsed}", sw.ElapsedMilliseconds);
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

        var sw = Stopwatch.StartNew();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        _logger.LogInformation("[TIMING] supabaseAdminDeleteUser http status={Status} elapsedMs={Elapsed}", (int)response.StatusCode, sw.ElapsedMilliseconds);

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

        var sw = Stopwatch.StartNew();
        User? user;
        try
        {
            user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
            _logger.LogInformation("[TIMING] getOrCreateUser step=lookup found={Found} elapsedMs={Elapsed}", user != null, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[TIMING] getOrCreateUser step=lookup FAILED type={Type} msg={Msg} elapsedMs={Elapsed}",
                ex.GetType().Name, ex.Message, sw.ElapsedMilliseconds);
            throw;
        }

        if (user == null)
        {
            user = new User
            {
                Id = userId,
                Email = email,
                Name = dto?.Name?.Trim() ?? User.FindFirstValue(ClaimTypes.Name) ?? email,
                AvatarUrl = dto?.AvatarUrl,
                Role = "user",
                Language = NormalizeLanguage(dto?.Language),
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

            if (!string.IsNullOrWhiteSpace(dto?.Language))
                user.Language = NormalizeLanguage(dto.Language);
        }

        sw.Restart();
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("[TIMING] getOrCreateUser step=saveChanges elapsedMs={Elapsed}", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[TIMING] getOrCreateUser step=saveChanges FAILED type={Type} msg={Msg} elapsedMs={Elapsed}",
                ex.GetType().Name, ex.Message, sw.ElapsedMilliseconds);
            throw;
        }
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
            IsBanned = user.IsBanned,
            Language = user.Language,
        };
    }

    // Mirrors the mobile app's normalizeLanguage() in i18n-provider.tsx — only
    // "en"/"sv" are supported, anything else (or missing) falls back to "en".
    private static string NormalizeLanguage(string? value)
        => value != null && value.Trim().ToLowerInvariant().StartsWith("sv") ? "sv" : "en";

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
            Role = User.FindFirstValue(ClaimTypes.Role) ?? "user",
            Language = NormalizeLanguage(dto?.Language),
        };
    }
}
