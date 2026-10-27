namespace HTBAM.Domain.Entities;

public sealed class CourseCodeCounter
{
    public long DepartmentId { get; set; }
    public int LastNumber { get; set; }
}

public sealed class CatalogSeedIdentity
{
    public string Key { get; set; } = "";
    public long? DepartmentId { get; set; }
    public long? CourseId { get; set; }
    public long? FacultyId { get; set; }
    public long? StudentClassId { get; set; }
    public long? RoomId { get; set; }
    public long? StudentId { get; set; }
}
