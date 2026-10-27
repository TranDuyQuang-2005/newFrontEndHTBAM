-- Additive metadata only. All existing teacher codes, technical IDs and relations are preserved.
IF OBJECT_ID('dbo.LecturerCodeSequences','U') IS NULL
    CREATE TABLE dbo.LecturerCodeSequences (
        DepartmentId bigint NOT NULL CONSTRAINT PK_LecturerCodeSequences PRIMARY KEY,
        LastNumber int NOT NULL CONSTRAINT CK_LecturerCodeSequences_LastNumber CHECK(LastNumber BETWEEN 0 AND 9999),
        UpdatedAt datetime2 NOT NULL,
        CONSTRAINT FK_LecturerCodeSequences_Departments FOREIGN KEY(DepartmentId) REFERENCES dbo.Departments(Id)
    );
GO
IF NOT EXISTS(SELECT 1 FROM sys.indexes i WHERE i.object_id=OBJECT_ID('dbo.Teachers') AND i.is_unique=1 AND i.has_filter=0 AND i.is_disabled=0
    AND (SELECT COUNT(*) FROM sys.index_columns ic WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal>0)=1
    AND EXISTS(SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
        WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal=1 AND c.name='TeacherCode'))
    CREATE UNIQUE INDEX UX_Teachers_TeacherCode ON dbo.Teachers(TeacherCode);
