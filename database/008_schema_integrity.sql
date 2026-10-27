/* Additive schema alignment. DEMO never claims official academic dates. */
SET XACT_ABORT ON;
GO
IF COL_LENGTH('dbo.AcademicTerms','IsDevelopment') IS NULL
 ALTER TABLE dbo.AcademicTerms ADD IsDevelopment bit NOT NULL CONSTRAINT DF_Term_Development DEFAULT 0;
IF COL_LENGTH('dbo.AcademicTerms','Status') IS NULL
 ALTER TABLE dbo.AcademicTerms ADD Status nvarchar(16) NOT NULL CONSTRAINT DF_Term_Status DEFAULT 'DRAFT';
IF COL_LENGTH('dbo.Sessions','IsDevelopment') IS NULL
 ALTER TABLE dbo.Sessions ADD IsDevelopment bit NOT NULL CONSTRAINT DF_Session_Development DEFAULT 0;
GO
IF EXISTS(SELECT 1 FROM sys.check_constraints WHERE name='CK_Session_Source' AND parent_object_id=OBJECT_ID('dbo.Sessions'))
 ALTER TABLE dbo.Sessions DROP CONSTRAINT CK_Session_Source;
ALTER TABLE dbo.Sessions ADD CONSTRAINT CK_Session_Source CHECK(
 (CameraId IS NOT NULL AND VideoId IS NULL) OR (CameraId IS NULL AND VideoId IS NOT NULL) OR
 (ClassScheduleId IS NOT NULL AND CameraId IS NULL AND VideoId IS NULL AND
 (Status IN('DRAFT','CANCELLED') OR (IsDevelopment=1 AND Status='COMPLETED'))));
IF NOT EXISTS(SELECT 1 FROM sys.check_constraints WHERE name='CK_Term_Status')
 ALTER TABLE dbo.AcademicTerms ADD CONSTRAINT CK_Term_Status CHECK(Status IN('DRAFT','ACTIVE','COMPLETED'));
IF NOT EXISTS(SELECT 1 FROM sys.foreign_keys WHERE name='FK_ClassSection_AttendancePolicy')
 ALTER TABLE dbo.ClassSections ADD CONSTRAINT FK_ClassSection_AttendancePolicy FOREIGN KEY(AttendancePolicyId) REFERENCES dbo.AttendancePolicies(Id);
IF NOT EXISTS(SELECT 1 FROM sys.foreign_keys WHERE name='FK_ClassGrant_GrantedByUser')
 ALTER TABLE dbo.ClassSectionGrants ADD CONSTRAINT FK_ClassGrant_GrantedByUser FOREIGN KEY(GrantedByUserId) REFERENCES dbo.Users(Id);
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='UX_BellVersion_Code' AND object_id=OBJECT_ID('dbo.BellTimeVersions'))
 CREATE UNIQUE INDEX UX_BellVersion_Code ON dbo.BellTimeVersions(Code);
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='UX_ScheduleException' AND object_id=OBJECT_ID('dbo.ScheduleExceptions'))
 CREATE UNIQUE INDEX UX_ScheduleException ON dbo.ScheduleExceptions(AcademicTermId,ClassSectionId,Date);
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_Schedule_Room_Dates' AND object_id=OBJECT_ID('dbo.ClassSchedules'))
 CREATE INDEX IX_Schedule_Room_Dates ON dbo.ClassSchedules(RoomId,StartsOn,EndsOn,IsActive);
GO
