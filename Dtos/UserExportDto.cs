namespace sidequest.backend.Dtos;

/// <summary>
/// GDPR-style data export for a single user. Includes all data the user is the
/// data subject of. Excludes other users' private profile data, secrets, tokens,
/// and internal system state.
/// </summary>
public class UserExportDto
{
    public DateTime ExportedAt { get; set; } = DateTime.UtcNow;
    public string SchemaVersion { get; set; } = "1.0";
    public ExportedUserDto User { get; set; } = null!;
    public List<ExportedTripDto> OwnedTrips { get; set; } = new();
    public List<ExportedTripDto> MemberTrips { get; set; } = new();
    public List<ExportedActivityDto> Activities { get; set; } = new();
    public List<ExportedCommentDto> ActivityComments { get; set; } = new();
    public List<ExportedChatMessageDto> ChatMessages { get; set; } = new();
    public ExportedExpensesDto Expenses { get; set; } = new();
    public List<ExportedSettlementDto> Settlements { get; set; } = new();
    public List<ExportedInviteDto> InvitesSent { get; set; } = new();
    public List<ExportedInviteDto> InvitesReceived { get; set; } = new();
    public List<ExportedFeedbackDto> Feedback { get; set; } = new();
    public List<ExportedTripEventDto> TripEvents { get; set; } = new();
    public List<string> UploadedImageUrls { get; set; } = new();
}

public class ExportedUserDto
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public string? Bio { get; set; }
    public string Role { get; set; } = "user";
    public bool HasCompletedOnboarding { get; set; }
    public string? FoundVia { get; set; }
    public string? Purpose { get; set; }
    public string? PurposeOtherText { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class ExportedTripDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Destination { get; set; } = string.Empty;
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string Visibility { get; set; } = "public";
    public DateTime? RevealAt { get; set; }
    public string? Teaser { get; set; }
    public string? ImageUrl { get; set; }
    public string? SpotifyUrl { get; set; }
    public string InviteCode { get; set; } = string.Empty;
    public string Status { get; set; } = "active";
    public string? ShareCode { get; set; }
    public DateTime? SharedAt { get; set; }
    public Guid OwnerId { get; set; }
    public DateTime CreatedAt { get; set; }

    // Only populated for member trips (when the user is NOT the owner)
    public DateTime? JoinedAt { get; set; }
    public bool? IsOwnerMembership { get; set; }
}

public class ExportedActivityDto
{
    public Guid Id { get; set; }
    public Guid TripId { get; set; }
    public DateOnly Date { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Time { get; set; }
    public string? Category { get; set; }
    public string? ImageUrl { get; set; }
    public string? SpotifyUrl { get; set; }
    public string Visibility { get; set; } = "public";
    public DateTime? RevealAt { get; set; }
    public string? Teaser { get; set; }
    public int? TeaserOffsetMinutes { get; set; }
    public bool IsHidden { get; set; }
    public Guid? AssignedToUserId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class ExportedCommentDto
{
    public Guid Id { get; set; }
    public Guid ActivityId { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public class ExportedChatMessageDto
{
    public Guid Id { get; set; }
    public Guid TripId { get; set; }
    public string Text { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public bool IsSystem { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class ExportedExpensesDto
{
    public List<ExportedExpenseDto> Created { get; set; } = new();
    public List<ExportedExpenseShareDto> PaidBy { get; set; } = new();
    public List<ExportedExpenseShareDto> ParticipatedIn { get; set; } = new();
}

public class ExportedExpenseDto
{
    public Guid Id { get; set; }
    public Guid TripId { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public DateOnly Date { get; set; }
    public string SplitMode { get; set; } = "equal";
    public string Currency { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public class ExportedExpenseShareDto
{
    public Guid ExpenseId { get; set; }
    public Guid TripId { get; set; }
    public decimal Amount { get; set; }
}

public class ExportedSettlementDto
{
    public Guid Id { get; set; }
    public Guid TripId { get; set; }
    public Guid FromUserId { get; set; }
    public Guid ToUserId { get; set; }
    public decimal Amount { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class ExportedInviteDto
{
    public Guid Id { get; set; }
    public Guid TripId { get; set; }
    public string Email { get; set; } = string.Empty;
    public Guid InvitedByUserId { get; set; }
    public string Status { get; set; } = "pending";
    public DateTime CreatedAt { get; set; }
}

public class ExportedFeedbackDto
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = "feedback";
    public string Message { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public class ExportedTripEventDto
{
    public Guid Id { get; set; }
    public Guid TripId { get; set; }
    public string Type { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
