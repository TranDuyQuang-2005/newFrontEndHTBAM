namespace HTBAM.Domain.Entities;

public sealed class LecturerCodeSequence
{
    public long DepartmentId { get; set; }
    public int LastNumber { get; set; }
    public DateTime UpdatedAt { get; set; }
}
