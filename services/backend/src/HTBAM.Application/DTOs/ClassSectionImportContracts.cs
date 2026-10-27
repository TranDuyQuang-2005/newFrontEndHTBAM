namespace HTBAM.Application.DTOs;

public record ImportOptions(long AcademicTermId, long AttendancePolicyId, int? Phase = null,
    int? HeaderRow = null, int? HeaderRowCount = null, Dictionary<string, int>? ColumnMappings = null,
    Dictionary<string, long>? CourseSelections = null, Dictionary<int, long>? TeacherSelections = null,
    Dictionary<int, long>? RoomSelections = null);
public record ImportColumn(string Field, string Label, int? ColumnIndex, bool Required);
public record ExcelColumn(int Index, string Letter, string Header, string Sample = "");
public record ImportSampleRow(int Row, string[] Cells);
public record ImportColumnGroup(string Field, string Label, ImportColumn[] Columns);
public record ImportSheetInspection(string Name, int RowCount, int ColumnCount, string Format,
    string FormatMessage, int HeaderRow, int HeaderRowCount, ExcelColumn[] Columns, ImportColumn[] Mappings, string[] MissingColumns)
{
    // HeaderRow remains an API compatibility alias. The scan limit is never the detected row.
    public int HeaderStartRow => HeaderRow;
    public int HeaderEndRow => HeaderStartRow + HeaderRowCount - 1;
    public int DataStartRow => HeaderEndRow + 1;
    public ImportSampleRow[] SampleRows { get; init; } = [];
    public ImportColumnGroup[] ColumnGroups => Mappings.Any(x => x.Field.StartsWith("week") && x.Field != "weeks")
        ? [new("weeks", "Các tuần có lịch", Mappings.Where(x => x.Field.StartsWith("week") && x.Field != "weeks").ToArray())] : [];
}
public record ImportWeek(int Number, DateOnly? StartsOn, DateOnly? EndsOn);
public record ImportConfirmRequest(string Token);
public sealed class ImportRow
{
    public int Row { get; set; }
    public string SourceClassSectionCode { get; set; } = "";
    public string Status { get; set; } = "VALID";
    public string? Column { get; set; }
    public string? Message { get; set; }
    public string Course { get; set; } = "";
    public string Teacher { get; set; } = "";
    public string Room { get; set; } = "";
    public string? StartsAt { get; set; }
    public string? EndsAt { get; set; }
    public ScheduleLineRequest? Schedule { get; set; }
    public long? CourseId { get; set; }
    public long? TeacherId { get; set; }
    public long? RoomId { get; set; }
    public string? CourseName { get; set; }
    public string? TeacherName { get; set; }
    public string? RoomName { get; set; }
    public int? Credits { get; set; }
    public string? ClassName { get; set; }
    public int? SourceSchedulePeriods { get; set; }
    public int? SourceTotalPeriods { get; set; }
    public ImportWeek[] Weeks { get; set; } = [];
    public List<ImportIssue> Issues { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public string? SourceCourseName { get; set; }
    public string? SourceTeacher { get; set; }
    public string? SourceRoom { get; set; }
}
public sealed class ImportGroup
{
    public string SourceClassSectionCode { get; set; } = "";
    public string Status { get; set; } = "VALID";
    public string? Message { get; set; }
    public ClassSectionRequest? Request { get; set; }
    public List<ImportRow> Rows { get; set; } = [];
    public string Key { get; set; } = "";
}
public sealed class ImportSnapshot
{
    public string Token { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public string Sheet { get; set; } = "";
    public string FileHash { get; set; } = "";
    public long AcademicTermId { get; set; }
    public DateOnly? WeekOneMonday { get; set; }
    public bool IsDevelopment { get; set; }
    public string WeekMessage { get; set; } = "";
    public int BlankRows { get; set; }
    public int WorkbookRows { get; set; }
    public int WorkbookColumns { get; set; }
    public int DataRows { get; set; }
    public string Format { get; set; } = "FLAT";
    public int HeaderRow { get; set; } = 1;
    public int HeaderRowCount { get; set; } = 1;
    public List<ImportGroup> Groups { get; set; } = [];
    public int ClassesToCreate => Groups.Count(x => x.Status is "VALID" or "WARNING");
    public int SchedulesToCreate => Groups.Where(x => x.Status is "VALID" or "WARNING").Sum(x => x.Request?.Schedules.Length ?? 0);
    public int ErrorRows => Groups.Where(x => x.Status == "ERROR").Sum(x => x.Rows.Count);
    public int Duplicates => Groups.SelectMany(x => x.Rows).Count(x => x.Status == "DUPLICATE");
    public int SkippedRows => BlankRows + Groups.Sum(x => x.Status == "ERROR" ? x.Rows.Count : x.Rows.Count(r => r.Status == "DUPLICATE"));
    public int WarningRows => Groups.SelectMany(x => x.Rows).Count(x => x.Status == "WARNING" || x.Warnings.Count > 0);
}
public record ImportGroupPage(string Key, string SourceClassSectionCode, string Status, string? Message, int TotalRows, ImportRow[] Rows);
public record ImportPreviewPage(string Token, DateTime ExpiresAt, string Sheet, string Format, int HeaderRow, int HeaderRowCount,
    int WorkbookRows, int WorkbookColumns, int DataRows, int TotalGroups, DateOnly? WeekOneMonday, bool IsDevelopment,
    string WeekMessage, int ClassesToCreate, int SchedulesToCreate, int SkippedRows, int ErrorRows, int WarningRows, int Duplicates,
    int Page, int PageSize, int MatchingRows, int MatchingGroups, ImportGroupPage[] Groups,
    int UnresolvedCourseRows, int UnresolvedTeacherRows, int UnresolvedRoomRows);
public record ImportIssue(int Row, string SourceClassSectionCode, string? Column, string Message);
public record ImportedClass(long Id, string Code, string SourceClassSectionCode);
public record ImportResult(int CreatedClasses, int UpdatedClasses, int CreatedSchedules, int SkippedRows,
    int Duplicates, ImportIssue[] Errors, ImportedClass[] Classes);
