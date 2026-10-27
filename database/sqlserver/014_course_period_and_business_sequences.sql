-- Additive upgrade: existing records, identifiers and business codes are preserved.
IF COL_LENGTH('dbo.Courses', 'StartYear') IS NULL ALTER TABLE dbo.Courses ADD StartYear int NULL;
IF COL_LENGTH('dbo.Courses', 'EndYear') IS NULL ALTER TABLE dbo.Courses ADD EndYear int NULL;
IF COL_LENGTH('dbo.Courses', 'StartSemester') IS NULL ALTER TABLE dbo.Courses ADD StartSemester varchar(3) NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_Courses_StartYears')
    ALTER TABLE dbo.Courses WITH CHECK ADD CONSTRAINT CK_Courses_StartYears CHECK
    ((StartYear IS NULL AND EndYear IS NULL) OR (StartYear IS NOT NULL AND EndYear IS NOT NULL AND StartYear BETWEEN 1000 AND 9998 AND EndYear = StartYear + 1));
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_Courses_StartSemester')
    ALTER TABLE dbo.Courses WITH CHECK ADD CONSTRAINT CK_Courses_StartSemester CHECK (StartSemester IS NULL OR StartSemester IN ('HK1','HK2'));
IF OBJECT_ID('dbo.BusinessCodeSequences','U') IS NULL
    CREATE TABLE dbo.BusinessCodeSequences (Scope nvarchar(200) NOT NULL PRIMARY KEY, LastNumber int NOT NULL,
        CONSTRAINT CK_BusinessCodeSequences_Number CHECK (LastNumber >= 0));
IF COL_LENGTH('dbo.Sessions', 'Code') IS NULL ALTER TABLE dbo.Sessions ADD Code nvarchar(128) NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.Sessions') AND name='IX_Sessions_Code')
    CREATE UNIQUE INDEX IX_Sessions_Code ON dbo.Sessions(Code) WHERE Code IS NOT NULL;
-- The other business-code unique indexes already exist in 001/008/012/013.
GO
-- Stable seed identities make replay independent of names and business codes.
IF COL_LENGTH('dbo.CatalogSeedIdentities','FacultyId') IS NULL
    ALTER TABLE dbo.CatalogSeedIdentities ADD FacultyId bigint NULL REFERENCES dbo.Faculties(Id);
IF COL_LENGTH('dbo.CatalogSeedIdentities','StudentClassId') IS NULL
    ALTER TABLE dbo.CatalogSeedIdentities ADD StudentClassId bigint NULL REFERENCES dbo.StudentClasses(Id);
IF COL_LENGTH('dbo.CatalogSeedIdentities','RoomId') IS NULL
    ALTER TABLE dbo.CatalogSeedIdentities ADD RoomId bigint NULL REFERENCES dbo.Rooms(Id);
IF COL_LENGTH('dbo.CatalogSeedIdentities','StudentId') IS NULL
    ALTER TABLE dbo.CatalogSeedIdentities ADD StudentId bigint NULL REFERENCES dbo.Students(Id);
GO
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name='CK_CatalogSeedIdentities_Target')
    ALTER TABLE dbo.CatalogSeedIdentities DROP CONSTRAINT CK_CatalogSeedIdentities_Target;
ALTER TABLE dbo.CatalogSeedIdentities WITH CHECK ADD CONSTRAINT CK_CatalogSeedIdentities_Target CHECK
    ((CASE WHEN DepartmentId IS NULL THEN 0 ELSE 1 END) + (CASE WHEN CourseId IS NULL THEN 0 ELSE 1 END) +
     (CASE WHEN FacultyId IS NULL THEN 0 ELSE 1 END) + (CASE WHEN StudentClassId IS NULL THEN 0 ELSE 1 END) +
     (CASE WHEN RoomId IS NULL THEN 0 ELSE 1 END) + (CASE WHEN StudentId IS NULL THEN 0 ELSE 1 END) = 1);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.CatalogSeedIdentities') AND name='UX_CatalogSeedIdentities_Faculty')
    CREATE UNIQUE INDEX UX_CatalogSeedIdentities_Faculty ON dbo.CatalogSeedIdentities(FacultyId) WHERE FacultyId IS NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.CatalogSeedIdentities') AND name='UX_CatalogSeedIdentities_StudentClass')
    CREATE UNIQUE INDEX UX_CatalogSeedIdentities_StudentClass ON dbo.CatalogSeedIdentities(StudentClassId) WHERE StudentClassId IS NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.CatalogSeedIdentities') AND name='UX_CatalogSeedIdentities_Room')
    CREATE UNIQUE INDEX UX_CatalogSeedIdentities_Room ON dbo.CatalogSeedIdentities(RoomId) WHERE RoomId IS NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.CatalogSeedIdentities') AND name='UX_CatalogSeedIdentities_Student')
    CREATE UNIQUE INDEX UX_CatalogSeedIdentities_Student ON dbo.CatalogSeedIdentities(StudentId) WHERE StudentId IS NOT NULL;
