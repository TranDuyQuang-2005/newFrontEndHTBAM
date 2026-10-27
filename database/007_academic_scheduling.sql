/* Additive upgrade after 001..006. No history deletion, code rewrite or timetable import. */
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
GO
BEGIN TRANSACTION;
IF COL_LENGTH('dbo.Rooms','RoomType') IS NULL ALTER TABLE dbo.Rooms ADD RoomType nvarchar(32) NOT NULL CONSTRAINT DF_Room_Type DEFAULT 'UNKNOWN';
IF COL_LENGTH('dbo.ClassSections','SourceCode') IS NULL ALTER TABLE dbo.ClassSections ADD SourceCode nvarchar(128) NULL;
IF COL_LENGTH('dbo.ClassSections','AcademicTermId') IS NULL ALTER TABLE dbo.ClassSections ADD AcademicTermId bigint NULL;
IF COL_LENGTH('dbo.ClassSections','Phase') IS NULL ALTER TABLE dbo.ClassSections ADD Phase int NULL;
IF COL_LENGTH('dbo.ClassSections','AttendancePolicyId') IS NULL ALTER TABLE dbo.ClassSections ADD AttendancePolicyId bigint NULL;
IF COL_LENGTH('dbo.ClassSections','Revision') IS NULL ALTER TABLE dbo.ClassSections ADD Revision int NOT NULL CONSTRAINT DF_Class_Revision DEFAULT 0;
IF COL_LENGTH('dbo.ClassSections','CreationKey') IS NULL ALTER TABLE dbo.ClassSections ADD CreationKey nvarchar(32) NULL;
IF COL_LENGTH('dbo.ClassSections','CreationRequestHash') IS NULL ALTER TABLE dbo.ClassSections ADD CreationRequestHash nvarchar(64) NULL;
IF COL_LENGTH('dbo.Enrollments','ClassNote') IS NULL ALTER TABLE dbo.Enrollments ADD ClassNote nvarchar(max) NULL;
IF COL_LENGTH('dbo.Students','SourceAnonymousCode') IS NULL ALTER TABLE dbo.Students ADD SourceAnonymousCode nvarchar(128) NULL;
IF OBJECT_ID('dbo.AcademicTerms','U') IS NULL
CREATE TABLE dbo.AcademicTerms(
 Id bigint IDENTITY PRIMARY KEY, AcademicYear nvarchar(16) NOT NULL, Semester int NOT NULL,
 StartsOn date NULL, EndsOn date NULL, DatesConfirmed bit NOT NULL DEFAULT 0,
 DivisionPolicy nvarchar(32) NOT NULL DEFAULT 'EXPLICIT', PhasesConfirmed bit NOT NULL DEFAULT 0,
 WeekOneMonday date NULL, WeekMappingConfirmed bit NOT NULL DEFAULT 0, Evidence nvarchar(max) NULL,
 CONSTRAINT UQ_AcademicTerm UNIQUE(AcademicYear,Semester), CONSTRAINT CK_AcademicTerm_Semester CHECK(Semester IN(1,2)),
 CONSTRAINT CK_AcademicTerm_Dates CHECK(EndsOn IS NULL OR StartsOn IS NULL OR EndsOn >= StartsOn)
);
IF OBJECT_ID('dbo.TermPhases','U') IS NULL
CREATE TABLE dbo.TermPhases(
 Id bigint IDENTITY PRIMARY KEY, AcademicTermId bigint NOT NULL REFERENCES dbo.AcademicTerms(Id),
 Credits int NOT NULL, Number int NOT NULL, StartsOn date NOT NULL, EndsOn date NOT NULL,
 CONSTRAINT UQ_TermPhase UNIQUE(AcademicTermId,Credits,Number),
 CONSTRAINT CK_TermPhase CHECK((Credits=1 AND Number BETWEEN 1 AND 3 OR Credits=2 AND Number BETWEEN 1 AND 2) AND EndsOn>=StartsOn)
);
IF OBJECT_ID('dbo.BellTimeVersions','U') IS NULL
CREATE TABLE dbo.BellTimeVersions(
 Id bigint IDENTITY PRIMARY KEY, Code nvarchar(64) NOT NULL, [Group] nvarchar(1) NOT NULL,
 EffectiveFrom date NOT NULL, EffectiveTo date NULL, Confirmed bit NOT NULL DEFAULT 0, Evidence nvarchar(max) NOT NULL,
 CONSTRAINT UQ_BellTimeVersion UNIQUE([Group],EffectiveFrom),
 CONSTRAINT CK_BellTimeVersion CHECK([Group] IN('A','B') AND (EffectiveTo IS NULL OR EffectiveTo>=EffectiveFrom))
);
IF OBJECT_ID('dbo.AcademicWeekCalendars','U') IS NULL
CREATE TABLE dbo.AcademicWeekCalendars(
 Id bigint IDENTITY PRIMARY KEY, AcademicTermId bigint NOT NULL REFERENCES dbo.AcademicTerms(Id),
 Name nvarchar(128) NOT NULL, WeekOneMonday date NOT NULL, Confirmed bit NOT NULL DEFAULT 0, Evidence nvarchar(max) NOT NULL,
 CONSTRAINT UQ_AcademicWeekCalendar UNIQUE(AcademicTermId,Name)
);
IF OBJECT_ID('dbo.BellPeriods','U') IS NULL
CREATE TABLE dbo.BellPeriods(
 Id bigint IDENTITY PRIMARY KEY, BellTimeVersionId bigint NOT NULL REFERENCES dbo.BellTimeVersions(Id),
 Number int NOT NULL, Shift int NOT NULL, StartsAt time NOT NULL, EndsAt time NOT NULL,
 CONSTRAINT UQ_BellPeriod UNIQUE(BellTimeVersionId,Number),
 CONSTRAINT CK_BellPeriod CHECK(Number BETWEEN 1 AND 16 AND Shift BETWEEN 1 AND 3 AND EndsAt>StartsAt)
);
IF OBJECT_ID('dbo.ClassSchedules','U') IS NULL
CREATE TABLE dbo.ClassSchedules(
 Id bigint IDENTITY PRIMARY KEY, ClassSectionId bigint NOT NULL REFERENCES dbo.ClassSections(Id),
 LineKey nvarchar(32) NOT NULL, Revision int NOT NULL, IsoDayOfWeek int NOT NULL,
 StartPeriod int NOT NULL, EndPeriod int NOT NULL, BellGroup nvarchar(1) NOT NULL,
 RoomId bigint NOT NULL REFERENCES dbo.Rooms(Id), ActivityType nvarchar(2) NOT NULL,
 StartsOn date NOT NULL, EndsOn date NOT NULL, SelectedWeeksJson nvarchar(max) NOT NULL DEFAULT '[]',
 SourceDataJson nvarchar(max) NULL, IsActive bit NOT NULL DEFAULT 1,
 CONSTRAINT UQ_ClassScheduleLine UNIQUE(ClassSectionId,Revision,LineKey),
 CONSTRAINT CK_ClassSchedule CHECK(IsoDayOfWeek BETWEEN 1 AND 7 AND StartPeriod BETWEEN 1 AND 16 AND EndPeriod BETWEEN StartPeriod AND 16 AND EndsOn>=StartsOn AND BellGroup IN('A','B') AND ActivityType IN('LT','TH'))
);
IF OBJECT_ID('dbo.ScheduleExceptions','U') IS NULL
CREATE TABLE dbo.ScheduleExceptions(
 Id bigint IDENTITY PRIMARY KEY, AcademicTermId bigint NOT NULL REFERENCES dbo.AcademicTerms(Id),
 ClassSectionId bigint NULL REFERENCES dbo.ClassSections(Id), Date date NOT NULL, Reason nvarchar(max) NOT NULL
);
GO
IF COL_LENGTH('dbo.AcademicTerms','Id') IS NULL EXEC(N'ALTER TABLE dbo.AcademicTerms ADD Id bigint IDENTITY PRIMARY KEY');
IF COL_LENGTH('dbo.AcademicTerms','AcademicYear') IS NULL EXEC(N'ALTER TABLE dbo.AcademicTerms ADD AcademicYear nvarchar(16) NOT NULL');
IF COL_LENGTH('dbo.AcademicTerms','Semester') IS NULL EXEC(N'ALTER TABLE dbo.AcademicTerms ADD Semester int NOT NULL');
IF COL_LENGTH('dbo.AcademicTerms','StartsOn') IS NULL EXEC(N'ALTER TABLE dbo.AcademicTerms ADD StartsOn date NULL');
IF COL_LENGTH('dbo.AcademicTerms','EndsOn') IS NULL EXEC(N'ALTER TABLE dbo.AcademicTerms ADD EndsOn date NULL');
IF COL_LENGTH('dbo.AcademicTerms','DatesConfirmed') IS NULL EXEC(N'ALTER TABLE dbo.AcademicTerms ADD DatesConfirmed bit NOT NULL DEFAULT 0');
IF COL_LENGTH('dbo.AcademicTerms','DivisionPolicy') IS NULL EXEC(N'ALTER TABLE dbo.AcademicTerms ADD DivisionPolicy nvarchar(32) NOT NULL DEFAULT ''EXPLICIT''');
IF COL_LENGTH('dbo.AcademicTerms','PhasesConfirmed') IS NULL EXEC(N'ALTER TABLE dbo.AcademicTerms ADD PhasesConfirmed bit NOT NULL DEFAULT 0');
IF COL_LENGTH('dbo.AcademicTerms','WeekOneMonday') IS NULL EXEC(N'ALTER TABLE dbo.AcademicTerms ADD WeekOneMonday date NULL');
IF COL_LENGTH('dbo.AcademicTerms','WeekMappingConfirmed') IS NULL EXEC(N'ALTER TABLE dbo.AcademicTerms ADD WeekMappingConfirmed bit NOT NULL DEFAULT 0');
IF COL_LENGTH('dbo.AcademicTerms','Evidence') IS NULL EXEC(N'ALTER TABLE dbo.AcademicTerms ADD Evidence nvarchar(max) NULL');
IF NOT EXISTS(SELECT 1 FROM sys.objects WHERE name='UQ_AcademicTerm' AND parent_object_id=OBJECT_ID('dbo.AcademicTerms')) EXEC(N'ALTER TABLE dbo.AcademicTerms ADD CONSTRAINT UQ_AcademicTerm UNIQUE(AcademicYear,Semester)');
IF NOT EXISTS(SELECT 1 FROM sys.objects WHERE name='CK_AcademicTerm_Semester' AND parent_object_id=OBJECT_ID('dbo.AcademicTerms')) EXEC(N'ALTER TABLE dbo.AcademicTerms ADD CONSTRAINT CK_AcademicTerm_Semester CHECK(Semester IN(1,2))');
IF NOT EXISTS(SELECT 1 FROM sys.objects WHERE name='CK_AcademicTerm_Dates' AND parent_object_id=OBJECT_ID('dbo.AcademicTerms')) EXEC(N'ALTER TABLE dbo.AcademicTerms ADD CONSTRAINT CK_AcademicTerm_Dates CHECK(EndsOn IS NULL OR StartsOn IS NULL OR EndsOn >= StartsOn)');
IF COL_LENGTH('dbo.TermPhases','Id') IS NULL EXEC(N'ALTER TABLE dbo.TermPhases ADD Id bigint IDENTITY PRIMARY KEY');
IF COL_LENGTH('dbo.TermPhases','AcademicTermId') IS NULL EXEC(N'ALTER TABLE dbo.TermPhases ADD AcademicTermId bigint NOT NULL REFERENCES dbo.AcademicTerms(Id)');
IF COL_LENGTH('dbo.TermPhases','Credits') IS NULL EXEC(N'ALTER TABLE dbo.TermPhases ADD Credits int NOT NULL');
IF COL_LENGTH('dbo.TermPhases','Number') IS NULL EXEC(N'ALTER TABLE dbo.TermPhases ADD Number int NOT NULL');
IF COL_LENGTH('dbo.TermPhases','StartsOn') IS NULL EXEC(N'ALTER TABLE dbo.TermPhases ADD StartsOn date NOT NULL');
IF COL_LENGTH('dbo.TermPhases','EndsOn') IS NULL EXEC(N'ALTER TABLE dbo.TermPhases ADD EndsOn date NOT NULL');
IF NOT EXISTS(SELECT 1 FROM sys.objects WHERE name='UQ_TermPhase' AND parent_object_id=OBJECT_ID('dbo.TermPhases')) EXEC(N'ALTER TABLE dbo.TermPhases ADD CONSTRAINT UQ_TermPhase UNIQUE(AcademicTermId,Credits,Number)');
IF NOT EXISTS(SELECT 1 FROM sys.objects WHERE name='CK_TermPhase' AND parent_object_id=OBJECT_ID('dbo.TermPhases')) EXEC(N'ALTER TABLE dbo.TermPhases ADD CONSTRAINT CK_TermPhase CHECK((Credits=1 AND Number BETWEEN 1 AND 3 OR Credits=2 AND Number BETWEEN 1 AND 2) AND EndsOn>=StartsOn)');
IF COL_LENGTH('dbo.BellTimeVersions','Id') IS NULL EXEC(N'ALTER TABLE dbo.BellTimeVersions ADD Id bigint IDENTITY PRIMARY KEY');
IF COL_LENGTH('dbo.BellTimeVersions','Code') IS NULL EXEC(N'ALTER TABLE dbo.BellTimeVersions ADD Code nvarchar(64) NOT NULL');
IF COL_LENGTH('dbo.BellTimeVersions','Group') IS NULL EXEC(N'ALTER TABLE dbo.BellTimeVersions ADD [Group] nvarchar(1) NOT NULL');
IF COL_LENGTH('dbo.BellTimeVersions','EffectiveFrom') IS NULL EXEC(N'ALTER TABLE dbo.BellTimeVersions ADD EffectiveFrom date NOT NULL');
IF COL_LENGTH('dbo.BellTimeVersions','EffectiveTo') IS NULL EXEC(N'ALTER TABLE dbo.BellTimeVersions ADD EffectiveTo date NULL');
IF COL_LENGTH('dbo.BellTimeVersions','Confirmed') IS NULL EXEC(N'ALTER TABLE dbo.BellTimeVersions ADD Confirmed bit NOT NULL DEFAULT 0');
IF COL_LENGTH('dbo.BellTimeVersions','Evidence') IS NULL EXEC(N'ALTER TABLE dbo.BellTimeVersions ADD Evidence nvarchar(max) NOT NULL');
IF NOT EXISTS(SELECT 1 FROM sys.objects WHERE name='UQ_BellTimeVersion' AND parent_object_id=OBJECT_ID('dbo.BellTimeVersions')) EXEC(N'ALTER TABLE dbo.BellTimeVersions ADD CONSTRAINT UQ_BellTimeVersion UNIQUE([Group],EffectiveFrom)');
IF NOT EXISTS(SELECT 1 FROM sys.objects WHERE name='CK_BellTimeVersion' AND parent_object_id=OBJECT_ID('dbo.BellTimeVersions')) EXEC(N'ALTER TABLE dbo.BellTimeVersions ADD CONSTRAINT CK_BellTimeVersion CHECK([Group] IN(''A'',''B'') AND (EffectiveTo IS NULL OR EffectiveTo>=EffectiveFrom))');
IF COL_LENGTH('dbo.AcademicWeekCalendars','Id') IS NULL EXEC(N'ALTER TABLE dbo.AcademicWeekCalendars ADD Id bigint IDENTITY PRIMARY KEY');
IF COL_LENGTH('dbo.AcademicWeekCalendars','AcademicTermId') IS NULL EXEC(N'ALTER TABLE dbo.AcademicWeekCalendars ADD AcademicTermId bigint NOT NULL REFERENCES dbo.AcademicTerms(Id)');
IF COL_LENGTH('dbo.AcademicWeekCalendars','Name') IS NULL EXEC(N'ALTER TABLE dbo.AcademicWeekCalendars ADD Name nvarchar(128) NOT NULL');
IF COL_LENGTH('dbo.AcademicWeekCalendars','WeekOneMonday') IS NULL EXEC(N'ALTER TABLE dbo.AcademicWeekCalendars ADD WeekOneMonday date NOT NULL');
IF COL_LENGTH('dbo.AcademicWeekCalendars','Confirmed') IS NULL EXEC(N'ALTER TABLE dbo.AcademicWeekCalendars ADD Confirmed bit NOT NULL DEFAULT 0');
IF COL_LENGTH('dbo.AcademicWeekCalendars','Evidence') IS NULL EXEC(N'ALTER TABLE dbo.AcademicWeekCalendars ADD Evidence nvarchar(max) NOT NULL');
IF NOT EXISTS(SELECT 1 FROM sys.objects WHERE name='UQ_AcademicWeekCalendar' AND parent_object_id=OBJECT_ID('dbo.AcademicWeekCalendars')) EXEC(N'ALTER TABLE dbo.AcademicWeekCalendars ADD CONSTRAINT UQ_AcademicWeekCalendar UNIQUE(AcademicTermId,Name)');
IF COL_LENGTH('dbo.BellPeriods','Id') IS NULL EXEC(N'ALTER TABLE dbo.BellPeriods ADD Id bigint IDENTITY PRIMARY KEY');
IF COL_LENGTH('dbo.BellPeriods','BellTimeVersionId') IS NULL EXEC(N'ALTER TABLE dbo.BellPeriods ADD BellTimeVersionId bigint NOT NULL REFERENCES dbo.BellTimeVersions(Id)');
IF COL_LENGTH('dbo.BellPeriods','Number') IS NULL EXEC(N'ALTER TABLE dbo.BellPeriods ADD Number int NOT NULL');
IF COL_LENGTH('dbo.BellPeriods','Shift') IS NULL EXEC(N'ALTER TABLE dbo.BellPeriods ADD Shift int NOT NULL');
IF COL_LENGTH('dbo.BellPeriods','StartsAt') IS NULL EXEC(N'ALTER TABLE dbo.BellPeriods ADD StartsAt time NOT NULL');
IF COL_LENGTH('dbo.BellPeriods','EndsAt') IS NULL EXEC(N'ALTER TABLE dbo.BellPeriods ADD EndsAt time NOT NULL');
IF NOT EXISTS(SELECT 1 FROM sys.objects WHERE name='UQ_BellPeriod' AND parent_object_id=OBJECT_ID('dbo.BellPeriods')) EXEC(N'ALTER TABLE dbo.BellPeriods ADD CONSTRAINT UQ_BellPeriod UNIQUE(BellTimeVersionId,Number)');
IF NOT EXISTS(SELECT 1 FROM sys.objects WHERE name='CK_BellPeriod' AND parent_object_id=OBJECT_ID('dbo.BellPeriods')) EXEC(N'ALTER TABLE dbo.BellPeriods ADD CONSTRAINT CK_BellPeriod CHECK(Number BETWEEN 1 AND 16 AND Shift BETWEEN 1 AND 3 AND EndsAt>StartsAt)');
IF COL_LENGTH('dbo.ClassSchedules','Id') IS NULL EXEC(N'ALTER TABLE dbo.ClassSchedules ADD Id bigint IDENTITY PRIMARY KEY');
IF COL_LENGTH('dbo.ClassSchedules','ClassSectionId') IS NULL EXEC(N'ALTER TABLE dbo.ClassSchedules ADD ClassSectionId bigint NOT NULL REFERENCES dbo.ClassSections(Id)');
IF COL_LENGTH('dbo.ClassSchedules','LineKey') IS NULL EXEC(N'ALTER TABLE dbo.ClassSchedules ADD LineKey nvarchar(32) NOT NULL');
IF COL_LENGTH('dbo.ClassSchedules','Revision') IS NULL EXEC(N'ALTER TABLE dbo.ClassSchedules ADD Revision int NOT NULL');
IF COL_LENGTH('dbo.ClassSchedules','IsoDayOfWeek') IS NULL EXEC(N'ALTER TABLE dbo.ClassSchedules ADD IsoDayOfWeek int NOT NULL');
IF COL_LENGTH('dbo.ClassSchedules','StartPeriod') IS NULL EXEC(N'ALTER TABLE dbo.ClassSchedules ADD StartPeriod int NOT NULL');
IF COL_LENGTH('dbo.ClassSchedules','EndPeriod') IS NULL EXEC(N'ALTER TABLE dbo.ClassSchedules ADD EndPeriod int NOT NULL');
IF COL_LENGTH('dbo.ClassSchedules','BellGroup') IS NULL EXEC(N'ALTER TABLE dbo.ClassSchedules ADD BellGroup nvarchar(1) NOT NULL');
IF COL_LENGTH('dbo.ClassSchedules','RoomId') IS NULL EXEC(N'ALTER TABLE dbo.ClassSchedules ADD RoomId bigint NOT NULL REFERENCES dbo.Rooms(Id)');
IF COL_LENGTH('dbo.ClassSchedules','ActivityType') IS NULL EXEC(N'ALTER TABLE dbo.ClassSchedules ADD ActivityType nvarchar(2) NOT NULL');
IF COL_LENGTH('dbo.ClassSchedules','StartsOn') IS NULL EXEC(N'ALTER TABLE dbo.ClassSchedules ADD StartsOn date NOT NULL');
IF COL_LENGTH('dbo.ClassSchedules','EndsOn') IS NULL EXEC(N'ALTER TABLE dbo.ClassSchedules ADD EndsOn date NOT NULL');
IF COL_LENGTH('dbo.ClassSchedules','SelectedWeeksJson') IS NULL EXEC(N'ALTER TABLE dbo.ClassSchedules ADD SelectedWeeksJson nvarchar(max) NOT NULL DEFAULT ''[]''');
IF COL_LENGTH('dbo.ClassSchedules','SourceDataJson') IS NULL EXEC(N'ALTER TABLE dbo.ClassSchedules ADD SourceDataJson nvarchar(max) NULL');
IF COL_LENGTH('dbo.ClassSchedules','IsActive') IS NULL EXEC(N'ALTER TABLE dbo.ClassSchedules ADD IsActive bit NOT NULL DEFAULT 1');
IF NOT EXISTS(SELECT 1 FROM sys.objects WHERE name='UQ_ClassScheduleLine' AND parent_object_id=OBJECT_ID('dbo.ClassSchedules')) EXEC(N'ALTER TABLE dbo.ClassSchedules ADD CONSTRAINT UQ_ClassScheduleLine UNIQUE(ClassSectionId,Revision,LineKey)');
IF NOT EXISTS(SELECT 1 FROM sys.objects WHERE name='CK_ClassSchedule' AND parent_object_id=OBJECT_ID('dbo.ClassSchedules')) EXEC(N'ALTER TABLE dbo.ClassSchedules ADD CONSTRAINT CK_ClassSchedule CHECK(IsoDayOfWeek BETWEEN 1 AND 7 AND StartPeriod BETWEEN 1 AND 16 AND EndPeriod BETWEEN StartPeriod AND 16 AND EndsOn>=StartsOn AND BellGroup IN(''A'',''B'') AND ActivityType IN(''LT'',''TH''))');
IF COL_LENGTH('dbo.ScheduleExceptions','Id') IS NULL EXEC(N'ALTER TABLE dbo.ScheduleExceptions ADD Id bigint IDENTITY PRIMARY KEY');
IF COL_LENGTH('dbo.ScheduleExceptions','AcademicTermId') IS NULL EXEC(N'ALTER TABLE dbo.ScheduleExceptions ADD AcademicTermId bigint NOT NULL REFERENCES dbo.AcademicTerms(Id)');
IF COL_LENGTH('dbo.ScheduleExceptions','ClassSectionId') IS NULL EXEC(N'ALTER TABLE dbo.ScheduleExceptions ADD ClassSectionId bigint NULL REFERENCES dbo.ClassSections(Id)');
IF COL_LENGTH('dbo.ScheduleExceptions','Date') IS NULL EXEC(N'ALTER TABLE dbo.ScheduleExceptions ADD Date date NOT NULL');
IF COL_LENGTH('dbo.ScheduleExceptions','Reason') IS NULL EXEC(N'ALTER TABLE dbo.ScheduleExceptions ADD Reason nvarchar(max) NOT NULL');
GO
IF COL_LENGTH('dbo.ClassSchedules','WeekCalendarId') IS NULL ALTER TABLE dbo.ClassSchedules ADD WeekCalendarId bigint NULL REFERENCES dbo.AcademicWeekCalendars(Id);
IF OBJECT_ID('dbo.ClassSectionGrants','U') IS NULL
CREATE TABLE dbo.ClassSectionGrants(
 Id bigint IDENTITY PRIMARY KEY, ClassSectionId bigint NOT NULL REFERENCES dbo.ClassSections(Id), TeacherId bigint NOT NULL REFERENCES dbo.Teachers(Id),
 CanAddDetails bit NOT NULL DEFAULT 0, CanEditDetails bit NOT NULL DEFAULT 0, CanDeleteDetails bit NOT NULL DEFAULT 0, CanManageStudents bit NOT NULL DEFAULT 0,
 GrantedByUserId bigint NOT NULL, UpdatedAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
 CONSTRAINT UQ_ClassSectionGrant UNIQUE(ClassSectionId,TeacherId)
);
GO
IF COL_LENGTH('dbo.ClassSectionGrants','Id') IS NULL EXEC(N'ALTER TABLE dbo.ClassSectionGrants ADD Id bigint IDENTITY PRIMARY KEY');
IF COL_LENGTH('dbo.ClassSectionGrants','ClassSectionId') IS NULL EXEC(N'ALTER TABLE dbo.ClassSectionGrants ADD ClassSectionId bigint NOT NULL REFERENCES dbo.ClassSections(Id)');
IF COL_LENGTH('dbo.ClassSectionGrants','TeacherId') IS NULL EXEC(N'ALTER TABLE dbo.ClassSectionGrants ADD TeacherId bigint NOT NULL REFERENCES dbo.Teachers(Id)');
IF COL_LENGTH('dbo.ClassSectionGrants','CanAddDetails') IS NULL EXEC(N'ALTER TABLE dbo.ClassSectionGrants ADD CanAddDetails bit NOT NULL DEFAULT 0');
IF COL_LENGTH('dbo.ClassSectionGrants','CanEditDetails') IS NULL EXEC(N'ALTER TABLE dbo.ClassSectionGrants ADD CanEditDetails bit NOT NULL DEFAULT 0');
IF COL_LENGTH('dbo.ClassSectionGrants','CanDeleteDetails') IS NULL EXEC(N'ALTER TABLE dbo.ClassSectionGrants ADD CanDeleteDetails bit NOT NULL DEFAULT 0');
IF COL_LENGTH('dbo.ClassSectionGrants','CanManageStudents') IS NULL EXEC(N'ALTER TABLE dbo.ClassSectionGrants ADD CanManageStudents bit NOT NULL DEFAULT 0');
IF COL_LENGTH('dbo.ClassSectionGrants','GrantedByUserId') IS NULL EXEC(N'ALTER TABLE dbo.ClassSectionGrants ADD GrantedByUserId bigint NOT NULL');
IF COL_LENGTH('dbo.ClassSectionGrants','UpdatedAt') IS NULL EXEC(N'ALTER TABLE dbo.ClassSectionGrants ADD UpdatedAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME()');
IF NOT EXISTS(SELECT 1 FROM sys.objects WHERE name='UQ_ClassSectionGrant' AND parent_object_id=OBJECT_ID('dbo.ClassSectionGrants')) EXEC(N'ALTER TABLE dbo.ClassSectionGrants ADD CONSTRAINT UQ_ClassSectionGrant UNIQUE(ClassSectionId,TeacherId)');
GO
IF COL_LENGTH('dbo.Sessions','ClassScheduleId') IS NULL ALTER TABLE dbo.Sessions ADD ClassScheduleId bigint NULL REFERENCES dbo.ClassSchedules(Id);
IF COL_LENGTH('dbo.Sessions','OccurrenceDate') IS NULL ALTER TABLE dbo.Sessions ADD OccurrenceDate date NULL;
IF COL_LENGTH('dbo.Sessions','StartPeriod') IS NULL ALTER TABLE dbo.Sessions ADD StartPeriod int NULL;
IF COL_LENGTH('dbo.Sessions','EndPeriod') IS NULL ALTER TABLE dbo.Sessions ADD EndPeriod int NULL;
IF COL_LENGTH('dbo.Sessions','BellTimeVersionId') IS NULL ALTER TABLE dbo.Sessions ADD BellTimeVersionId bigint NULL REFERENCES dbo.BellTimeVersions(Id);
IF COL_LENGTH('dbo.Sessions','SupersededByScheduleChange') IS NULL ALTER TABLE dbo.Sessions ADD SupersededByScheduleChange bit NOT NULL CONSTRAINT DF_Session_Superseded DEFAULT 0;
IF EXISTS(SELECT 1 FROM sys.check_constraints WHERE name='CK_Session_Source' AND parent_object_id=OBJECT_ID('dbo.Sessions')) ALTER TABLE dbo.Sessions DROP CONSTRAINT CK_Session_Source;
EXEC(N'ALTER TABLE dbo.Sessions ADD CONSTRAINT CK_Session_Source CHECK((CameraId IS NOT NULL AND VideoId IS NULL) OR (CameraId IS NULL AND VideoId IS NOT NULL) OR (ClassScheduleId IS NOT NULL AND Status IN(''DRAFT'',''CANCELLED'') AND CameraId IS NULL AND VideoId IS NULL));');
IF NOT EXISTS(SELECT 1 FROM sys.foreign_keys WHERE name='FK_ClassSection_Term') ALTER TABLE dbo.ClassSections ADD CONSTRAINT FK_ClassSection_Term FOREIGN KEY(AcademicTermId) REFERENCES dbo.AcademicTerms(Id);
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='UX_ClassSection_CreationKey' AND object_id=OBJECT_ID('dbo.ClassSections')) EXEC(N'CREATE UNIQUE INDEX UX_ClassSection_CreationKey ON dbo.ClassSections(CreationKey) WHERE CreationKey IS NOT NULL;');
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='UX_Session_Occurrence' AND object_id=OBJECT_ID('dbo.Sessions')) EXEC(N'CREATE UNIQUE INDEX UX_Session_Occurrence ON dbo.Sessions(ClassScheduleId,OccurrenceDate) WHERE ClassScheduleId IS NOT NULL AND OccurrenceDate IS NOT NULL;');
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='UX_Student_SourceAnonymousCode' AND object_id=OBJECT_ID('dbo.Students')) EXEC(N'CREATE UNIQUE INDEX UX_Student_SourceAnonymousCode ON dbo.Students(SourceAnonymousCode) WHERE SourceAnonymousCode IS NOT NULL AND SourceAnonymousCode <> '''';');
COMMIT;
GO

/* Only facts in the supplied notice. These terms deliberately cannot generate official schedules yet. */
IF NOT EXISTS(SELECT 1 FROM dbo.AcademicTerms WHERE AcademicYear='2025-2026' AND Semester=1)
 INSERT dbo.AcademicTerms(AcademicYear,Semester,Evidence) VALUES('2025-2026',1,N'Excel dự kiến HK1 không xác nhận ngày học kỳ chính thức.');
IF NOT EXISTS(SELECT 1 FROM dbo.AcademicTerms WHERE AcademicYear='2025-2026' AND Semester=2)
 INSERT dbo.AcademicTerms(AcademicYear,Semester,StartsOn,Evidence) VALUES('2025-2026',2,'20260112',N'Thông báo HUIT: HK2 bắt đầu 12/01/2026. Chưa có ngày kết thúc/giai đoạn chính thức.');
GO
BEGIN TRANSACTION;
DECLARE @Group nvarchar(1), @Id bigint;
DECLARE groups_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT g FROM (VALUES(N'A'),(N'B')) AS groups_data(g);
OPEN groups_cursor; FETCH NEXT FROM groups_cursor INTO @Group;
WHILE @@FETCH_STATUS=0
BEGIN
 IF NOT EXISTS(SELECT 1 FROM dbo.BellTimeVersions WHERE [Group]=@Group AND EffectiveFrom='20260112')
 BEGIN
  INSERT dbo.BellTimeVersions(Code,[Group],EffectiveFrom,Confirmed,Evidence) VALUES('HUIT-2026-01-12-'+@Group,@Group,'20260112',1,N'Ảnh thông báo HUIT: bảng giờ học, không dùng bảng giờ thi.');
  SET @Id=SCOPE_IDENTITY();
  IF @Group=N'A'
   INSERT dbo.BellPeriods(BellTimeVersionId,Number,Shift,StartsAt,EndsAt) VALUES
    (@Id,1,1,'06:30','07:20'),(@Id,2,1,'07:20','08:10'),(@Id,3,1,'08:10','09:00'),(@Id,4,1,'09:30','10:20'),(@Id,5,1,'10:20','11:10'),(@Id,6,1,'11:10','12:00'),
    (@Id,7,2,'12:30','13:20'),(@Id,8,2,'13:20','14:10'),(@Id,9,2,'14:10','15:00'),(@Id,10,2,'15:30','16:20'),(@Id,11,2,'16:20','17:10'),(@Id,12,2,'17:10','18:00'),
    (@Id,13,3,'18:10','19:00'),(@Id,14,3,'19:00','19:50'),(@Id,15,3,'19:50','20:40'),(@Id,16,3,'20:40','21:30');
  ELSE
   INSERT dbo.BellPeriods(BellTimeVersionId,Number,Shift,StartsAt,EndsAt) VALUES
    (@Id,1,1,'07:00','07:50'),(@Id,2,1,'07:50','08:40'),(@Id,3,1,'08:40','09:30'),(@Id,4,1,'09:30','10:20'),(@Id,5,1,'10:20','11:10'),(@Id,6,1,'11:10','12:00'),
    (@Id,7,2,'12:30','13:20'),(@Id,8,2,'13:20','14:10'),(@Id,9,2,'14:10','15:00'),(@Id,10,2,'15:00','15:50'),(@Id,11,2,'15:50','16:40'),(@Id,12,2,'16:40','17:30'),
    (@Id,13,3,'18:00','18:50'),(@Id,14,3,'18:50','19:40'),(@Id,15,3,'19:40','20:30'),(@Id,16,3,'20:30','21:20');
 END
 FETCH NEXT FROM groups_cursor INTO @Group;
END
CLOSE groups_cursor; DEALLOCATE groups_cursor;
COMMIT;
GO
