/* Complete relationships when an earlier deployment added a column without its FK.
   Detect equivalent FKs by metadata rather than relying on generated constraint names. */
SET XACT_ABORT ON;
GO
DECLARE @Table sysname, @Column sysname, @Target sysname, @Name sysname, @Sql nvarchar(max);
DECLARE relation_cursor CURSOR LOCAL FAST_FORWARD FOR
 SELECT * FROM (VALUES
 ('Sessions','ClassScheduleId','ClassSchedules','FK_Session_ClassSchedule_Repair'),
 ('Sessions','BellTimeVersionId','BellTimeVersions','FK_Session_BellVersion_Repair'),
 ('ClassSchedules','WeekCalendarId','AcademicWeekCalendars','FK_Schedule_WeekCalendar_Repair'),
 ('ClassSchedules','ClassSectionId','ClassSections','FK_Schedule_Class_Repair'),
 ('ClassSchedules','RoomId','Rooms','FK_Schedule_Room_Repair'),
 ('TermPhases','AcademicTermId','AcademicTerms','FK_Phase_Term_Repair'),
 ('AcademicWeekCalendars','AcademicTermId','AcademicTerms','FK_Week_Term_Repair'),
 ('BellPeriods','BellTimeVersionId','BellTimeVersions','FK_Period_Version_Repair'),
 ('ScheduleExceptions','AcademicTermId','AcademicTerms','FK_Exception_Term_Repair'),
 ('ScheduleExceptions','ClassSectionId','ClassSections','FK_Exception_Class_Repair'),
 ('ClassSectionGrants','TeacherId','Teachers','FK_Grant_Teacher_Repair'),
 ('ClassSectionGrants','ClassSectionId','ClassSections','FK_Grant_Class_Repair')
 ) AS r(TableName,ColumnName,TargetName,ConstraintName);
OPEN relation_cursor;
FETCH NEXT FROM relation_cursor INTO @Table,@Column,@Target,@Name;
WHILE @@FETCH_STATUS=0
BEGIN
 IF NOT EXISTS(SELECT 1 FROM sys.foreign_key_columns f
   WHERE f.parent_object_id=OBJECT_ID('dbo.'+@Table)
   AND COL_NAME(f.parent_object_id,f.parent_column_id)=@Column
   AND f.referenced_object_id=OBJECT_ID('dbo.'+@Target))
 BEGIN
  SET @Sql=N'ALTER TABLE dbo.'+QUOTENAME(@Table)+N' WITH CHECK ADD CONSTRAINT '+QUOTENAME(@Name)+N' FOREIGN KEY('+QUOTENAME(@Column)+N') REFERENCES dbo.'+QUOTENAME(@Target)+N'(Id);';
  EXEC sp_executesql @Sql;
 END;
 FETCH NEXT FROM relation_cursor INTO @Table,@Column,@Target,@Name;
END;
CLOSE relation_cursor;
DEALLOCATE relation_cursor;
GO
