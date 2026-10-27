-- Additive only: preserve existing IDs, codes, schedules and source values.
IF COL_LENGTH('dbo.ClassSections','SourceCode') IS NULL
    ALTER TABLE dbo.ClassSections ADD SourceCode nvarchar(128) NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.ClassSections') AND is_unique=1 AND name='UX_ClassSections_SourceCode_Term')
    CREATE UNIQUE INDEX UX_ClassSections_SourceCode_Term ON dbo.ClassSections(AcademicTermId,SourceCode)
    WHERE SourceCode IS NOT NULL AND SourceCode <> '';
IF NOT EXISTS (SELECT 1 FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id WHERE i.object_id=OBJECT_ID('dbo.ClassSections') AND i.is_unique=1 AND c.name='Code' AND NOT EXISTS (SELECT 1 FROM sys.index_columns ic2 WHERE ic2.object_id=i.object_id AND ic2.index_id=i.index_id AND ic2.key_ordinal>1))
    CREATE UNIQUE INDEX UX_ClassSections_Code ON dbo.ClassSections(Code);
IF OBJECT_ID('dbo.ClassSectionCounters','U') IS NULL
BEGIN
    CREATE TABLE dbo.ClassSectionCounters(
        Id bigint IDENTITY PRIMARY KEY,
        AcademicTermId bigint NOT NULL REFERENCES dbo.AcademicTerms(Id),
        CourseId bigint NOT NULL REFERENCES dbo.Courses(Id),
        LastNumber int NOT NULL CHECK(LastNumber BETWEEN 0 AND 99),
        CONSTRAINT UX_ClassSectionCounters_Term_Course UNIQUE(AcademicTermId,CourseId));
END;
IF OBJECT_ID('dbo.ClassSectionImportPreviews','U') IS NULL
BEGIN
    CREATE TABLE dbo.ClassSectionImportPreviews(
        Id bigint IDENTITY PRIMARY KEY,
        Token varchar(64) NOT NULL,
        UserId bigint NOT NULL REFERENCES dbo.Users(Id),
        ExpiresAt datetime2 NOT NULL,
        SnapshotJson nvarchar(max) NOT NULL,
        ResultJson nvarchar(max) NULL,
        CONSTRAINT UX_ClassSectionImportPreviews_Token UNIQUE(Token));
END;
-- AcademicTerms already stores year/semester, WeekOneMonday, confirmation and DEMO flags.
-- Reuse these fields; no new group concept or inferred dates.
