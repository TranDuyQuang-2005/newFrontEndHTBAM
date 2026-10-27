using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace HTBAM.Application.DTOs;

public record TermPhaseRequest(int Credits, int Number, DateOnly StartsOn, DateOnly EndsOn);
public record AcademicTermRequest(
    [Required, RegularExpression(@"^\d{4}-\d{4}$")] string AcademicYear,
    [Range(1, 2)] int Semester, DateOnly? StartsOn, DateOnly? EndsOn,
    bool DatesConfirmed, string DivisionPolicy, bool PhasesConfirmed,
    DateOnly? WeekOneMonday, bool WeekMappingConfirmed, string? Evidence,
    TermPhaseRequest[] Phases);

public record ScheduleLineRequest(
    [Required, RegularExpression("^[a-fA-F0-9]{32}$")] string LineKey,
    [Range(1, 7)] int IsoDayOfWeek, [Range(1, 16)] int StartPeriod, [Range(1, 16)] int EndPeriod,
    string? BellGroup, long RoomId, string ActivityType,
    DateOnly StartsOn, DateOnly EndsOn, int[] SelectedWeeks, long? WeekCalendarId = null);

public record ClassSectionRequest(
    [Required, StringLength(256)] string Name,
    long CourseId, long TeacherId, long AcademicTermId, int? Phase,
    long AttendancePolicyId, string? Note, string? SourceClassSectionCode,
    ScheduleLineRequest[] Schedules, int Revision = 0,
    string? CreationKey = null, string? PreviewToken = null)
{
    // Accept previously shipped clients, but emit only the canonical field name.
    [JsonPropertyName("sourceCode"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacySourceCode
    {
        get => null;
        init
        {
            if (SourceClassSectionCode is null) SourceClassSectionCode = value;
            else if (value is not null && value != SourceClassSectionCode) throw new ArgumentException("Hai trường mã nguồn không khớp.");
        }
    }
}

public record ClassGrantRequest(bool CanAddDetails, bool CanEditDetails, bool CanDeleteDetails, bool CanManageStudents);
public record ClassRosterRequest(long[] StudentIds);
public record ClassStudentNoteRequest([StringLength(2000)] string? Note);
public record ScheduleExceptionRequest(long AcademicTermId, long? ClassSectionId, DateOnly Date, [Required] string Reason);
public record BellPeriodRequest(int Number, int Shift, TimeOnly StartsAt, TimeOnly EndsAt);
public record BellVersionRequest([Required] string Code, [Required] string Group, DateOnly EffectiveFrom, DateOnly? EffectiveTo, [Required] string Evidence, bool Confirmed, BellPeriodRequest[] Periods);
public record WeekCalendarRequest(long AcademicTermId, [Required] string Name, DateOnly WeekOneMonday, bool Confirmed, [Required] string Evidence);
public record WeekOneRequest(DateOnly WeekOneMonday, bool Confirmed, [Required] string Evidence);
