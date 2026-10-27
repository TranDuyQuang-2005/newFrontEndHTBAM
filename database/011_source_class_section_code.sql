-- Rename metadata only. Keep all class IDs, internal codes and source values.
IF COL_LENGTH('dbo.ClassSections','SourceCode') IS NOT NULL
   AND COL_LENGTH('dbo.ClassSections','SourceClassSectionCode') IS NOT NULL
    THROW 51011, 'Both source columns exist. Reconcile explicitly before migration 011.', 1;
IF COL_LENGTH('dbo.ClassSections','SourceCode') IS NOT NULL
BEGIN
    IF EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.ClassSections') AND name='UX_ClassSections_SourceCode_Term')
        DROP INDEX UX_ClassSections_SourceCode_Term ON dbo.ClassSections;
    EXEC sys.sp_rename N'dbo.ClassSections.SourceCode', N'SourceClassSectionCode', N'COLUMN';
END;
GO
IF COL_LENGTH('dbo.ClassSections','SourceClassSectionCode') IS NULL
    THROW 51011, 'ClassSections.SourceClassSectionCode is missing. Run migration 010 first.', 1;
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.ClassSections') AND name='UX_ClassSections_SourceClassSectionCode_Term')
    CREATE UNIQUE INDEX UX_ClassSections_SourceClassSectionCode_Term
    ON dbo.ClassSections(AcademicTermId,SourceClassSectionCode)
    WHERE SourceClassSectionCode IS NOT NULL AND SourceClassSectionCode <> '';
