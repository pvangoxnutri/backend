namespace sidequest.backend.Dtos;

/// <summary>
/// Drag-to-move across days: the activity's new date plus the TARGET day's
/// full ordered id list (which must include the moved activity itself).
/// </summary>
public class MoveActivityDto
{
    public DateOnly Date { get; set; }
    public List<Guid> ActivityIds { get; set; } = new();
}
