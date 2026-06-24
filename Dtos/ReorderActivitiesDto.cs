namespace sidequest.backend.Dtos;

public class ReorderActivitiesDto
{
    public DateOnly Date { get; set; }
    public List<Guid> ActivityIds { get; set; } = new();
}
