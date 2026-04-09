using System.ComponentModel.DataAnnotations;

namespace sidequest.backend.Dtos;

public class CreateCommentDto
{
    [Required]
    [MaxLength(1000)]
    public string Text { get; set; } = string.Empty;
}
