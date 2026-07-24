using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace sidequest.backend.Dtos;

public class CreateTripDocumentDto
{
    [Required]
    public IFormFile File { get; set; } = null!;

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(40)]
    public string Category { get; set; } = string.Empty;

    public DateOnly? Date { get; set; }
    public Guid? ActivityId { get; set; }

    [MaxLength(2000)]
    public string? Note { get; set; }

    [MaxLength(200)]
    public string? BookingReference { get; set; }
}

public class UpdateTripDocumentDto
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(40)]
    public string Category { get; set; } = string.Empty;

    public DateOnly? Date { get; set; }
    public Guid? ActivityId { get; set; }

    [MaxLength(2000)]
    public string? Note { get; set; }

    [MaxLength(200)]
    public string? BookingReference { get; set; }
}

public class TripDocumentDto
{
    public Guid Id { get; set; }
    public Guid TripId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string FileType { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateOnly? Date { get; set; }
    public Guid? ActivityId { get; set; }
    public string? ActivityTitle { get; set; }
    public string? Note { get; set; }
    public string? BookingReference { get; set; }
    public Guid CreatedByUserId { get; set; }
    public string CreatedByName { get; set; } = string.Empty;
    public DateTime UploadedAt { get; set; }
    public bool CanEdit { get; set; }
}

public class TripDocumentOpenResponseDto
{
    public string Url { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
}
