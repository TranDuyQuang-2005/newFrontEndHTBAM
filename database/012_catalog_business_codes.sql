-- Additive only: do not rename, renumber or remove any existing catalog record.
IF OBJECT_ID('dbo.CourseCodeCounters','U') IS NULL
    CREATE TABLE dbo.CourseCodeCounters (
        DepartmentId bigint NOT NULL CONSTRAINT PK_CourseCodeCounters PRIMARY KEY,
        LastNumber int NOT NULL CONSTRAINT CK_CourseCodeCounters_LastNumber CHECK(LastNumber BETWEEN 0 AND 999),
        CONSTRAINT FK_CourseCodeCounters_Departments FOREIGN KEY(DepartmentId) REFERENCES dbo.Departments(Id)
    );
GO
IF OBJECT_ID('dbo.CatalogSeedIdentities','U') IS NULL
BEGIN
    CREATE TABLE dbo.CatalogSeedIdentities (
        [Key] nvarchar(64) NOT NULL CONSTRAINT PK_CatalogSeedIdentities PRIMARY KEY,
        DepartmentId bigint NULL CONSTRAINT FK_CatalogSeedIdentities_Departments REFERENCES dbo.Departments(Id),
        CourseId bigint NULL CONSTRAINT FK_CatalogSeedIdentities_Courses REFERENCES dbo.Courses(Id),
        CONSTRAINT CK_CatalogSeedIdentities_Target CHECK((DepartmentId IS NOT NULL AND CourseId IS NULL) OR (DepartmentId IS NULL AND CourseId IS NOT NULL))
    );
    CREATE UNIQUE INDEX UX_CatalogSeedIdentities_Department ON dbo.CatalogSeedIdentities(DepartmentId) WHERE DepartmentId IS NOT NULL;
    CREATE UNIQUE INDEX UX_CatalogSeedIdentities_Course ON dbo.CatalogSeedIdentities(CourseId) WHERE CourseId IS NOT NULL;
END;
GO
-- Reuse an existing unfiltered unique index/constraint on Code, regardless of its name.
IF NOT EXISTS (SELECT 1 FROM sys.indexes i WHERE i.object_id=OBJECT_ID('dbo.Departments') AND i.is_unique=1 AND i.has_filter=0
    AND (SELECT COUNT(*) FROM sys.index_columns ic WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal>0)=1
    AND EXISTS(SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal=1 AND c.name='Code'))
    CREATE UNIQUE INDEX UX_Departments_Code ON dbo.Departments(Code);
IF NOT EXISTS (SELECT 1 FROM sys.indexes i WHERE i.object_id=OBJECT_ID('dbo.Courses') AND i.is_unique=1 AND i.has_filter=0
    AND (SELECT COUNT(*) FROM sys.index_columns ic WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal>0)=1
    AND EXISTS(SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal=1 AND c.name='Code'))
    CREATE UNIQUE INDEX UX_Courses_Code ON dbo.Courses(Code);
