namespace HTBAM.Domain.Entities;

public sealed class ClassSectionCounter : Entity
{
    public long AcademicTermId { get; set; }
    public long CourseId { get; set; }
    public int LastNumber { get; set; }
}

public sealed class ClassSectionImportPreview : Entity
{
    public string Token { get; set; } = "";
    public long UserId { get; set; }
    public DateTime ExpiresAt { get; set; }
    public string SnapshotJson { get; set; } = "";
    public string? ResultJson { get; set; }
}

public sealed class AcademicTerm : Entity
{
    public bool IsDevelopment { get; set; }
    public string Status { get; set; } = "DRAFT";
    public string AcademicYear { get; set; } = "";
    public int Semester { get; set; }
    public DateOnly? StartsOn { get; set; }
    public DateOnly? EndsOn { get; set; }
    public bool DatesConfirmed { get; set; }
    public string DivisionPolicy { get; set; } = "EXPLICIT";
    public bool PhasesConfirmed { get; set; }
    public DateOnly? WeekOneMonday { get; set; }
    public bool WeekMappingConfirmed { get; set; }
    public string? Evidence { get; set; }
}

public sealed class TermPhase : Entity
{
    public long AcademicTermId { get; set; }
    public AcademicTerm AcademicTerm { get; set; } = null!;
    public int Credits { get; set; }
    public int Number { get; set; }
    public DateOnly StartsOn { get; set; }
    public DateOnly EndsOn { get; set; }
}

public sealed class AcademicWeekCalendar : Entity
{
    public long AcademicTermId { get; set; }
    public AcademicTerm AcademicTerm { get; set; } = null!;
    public string Name { get; set; } = "";
    public DateOnly WeekOneMonday { get; set; }
    public bool Confirmed { get; set; }
    public string Evidence { get; set; } = "";
}

public sealed class BellTimeVersion : Entity
{
    public string Code { get; set; } = "";
    public string Group { get; set; } = "";
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public bool Confirmed { get; set; }
    public string Evidence { get; set; } = "";
}

public sealed class BellPeriod : Entity
{
    public long BellTimeVersionId { get; set; }
    public BellTimeVersion BellTimeVersion { get; set; } = null!;
    public int Number { get; set; }
    public int Shift { get; set; }
    public TimeOnly StartsAt { get; set; }
    public TimeOnly EndsAt { get; set; }
}

public sealed class ClassSchedule : Entity
{
    public long? WeekCalendarId { get; set; }
    public long ClassSectionId { get; set; }
    public ClassSection ClassSection { get; set; } = null!;
    public string LineKey { get; set; } = "";
    public int Revision { get; set; }
    public int IsoDayOfWeek { get; set; }
    public int StartPeriod { get; set; }
    public int EndPeriod { get; set; }
    public string BellGroup { get; set; } = "";
    public long RoomId { get; set; }
    public Room Room { get; set; } = null!;
    public string ActivityType { get; set; } = "LT";
    public DateOnly StartsOn { get; set; }
    public DateOnly EndsOn { get; set; }
    public string SelectedWeeksJson { get; set; } = "[]";
    public string? SourceDataJson { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class ScheduleException : Entity
{
    public long AcademicTermId { get; set; }
    public AcademicTerm AcademicTerm { get; set; } = null!;
    public long? ClassSectionId { get; set; }
    public ClassSection? ClassSection { get; set; }
    public DateOnly Date { get; set; }
    public string Reason { get; set; } = "";
}

// Extends the existing teacher/position model; never a global role or permission.
public sealed class ClassSectionGrant : Entity
{
    public long ClassSectionId { get; set; }
    public ClassSection ClassSection { get; set; } = null!;
    public long TeacherId { get; set; }
    public Teacher Teacher { get; set; } = null!;
    public bool CanAddDetails { get; set; }
    public bool CanEditDetails { get; set; }
    public bool CanDeleteDetails { get; set; }
    public bool CanManageStudents { get; set; }
    public long GrantedByUserId { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
