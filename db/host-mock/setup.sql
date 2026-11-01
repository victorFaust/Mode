-- Local stand-in for the host application's users table. Run via scripts/dev-setup.ps1.
-- sqlcmd variables: HostDatabase, ReaderPassword
SET NOCOUNT ON;
GO
IF DB_ID(N'$(HostDatabase)') IS NULL
    CREATE DATABASE [$(HostDatabase)];
GO
USE [$(HostDatabase)];
GO
IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Users
    (
        Id          uniqueidentifier NOT NULL CONSTRAINT PK_Users PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
        DisplayName nvarchar(200)    NOT NULL,
        Email       nvarchar(320)    NOT NULL CONSTRAINT UQ_Users_Email UNIQUE,
        AvatarUrl   nvarchar(500)    NULL,
        IsActive    bit              NOT NULL CONSTRAINT DF_Users_IsActive DEFAULT (1)
    );
END
GO
MERGE dbo.Users AS target
USING (VALUES
    ('0a1b0001-0000-4000-8000-000000000001', N'Ada Okafor',     N'ada.okafor@uba.example',     1),
    ('0a1b0001-0000-4000-8000-000000000002', N'Bola Adeyemi',   N'bola.adeyemi@uba.example',   1),
    ('0a1b0001-0000-4000-8000-000000000003', N'Chidi Nwosu',    N'chidi.nwosu@uba.example',    1),
    ('0a1b0001-0000-4000-8000-000000000004', N'Deborah Eze',    N'deborah.eze@uba.example',    1),
    ('0a1b0001-0000-4000-8000-000000000005', N'Emeka Obi',      N'emeka.obi@uba.example',      1),
    ('0a1b0001-0000-4000-8000-000000000006', N'Funke Bello',    N'funke.bello@uba.example',    1),
    ('0a1b0001-0000-4000-8000-000000000007', N'Grace Johnson',  N'grace.johnson@uba.example',  1),
    ('0a1b0001-0000-4000-8000-000000000008', N'Hassan Musa',    N'hassan.musa@uba.example',    1),
    ('0a1b0001-0000-4000-8000-000000000009', N'Ifeoma Uche',    N'ifeoma.uche@uba.example',    0),
    ('0a1b0001-0000-4000-8000-000000000010', N'Jide Lawal',     N'jide.lawal@uba.example',     1)
) AS source (Id, DisplayName, Email, IsActive)
ON target.Id = source.Id
WHEN MATCHED THEN
    UPDATE SET DisplayName = source.DisplayName, Email = source.Email, IsActive = source.IsActive
WHEN NOT MATCHED THEN
    INSERT (Id, DisplayName, Email, IsActive) VALUES (source.Id, source.DisplayName, source.Email, source.IsActive);
GO
USE [master];
GO
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'mode_reader')
    CREATE LOGIN [mode_reader] WITH PASSWORD = N'$(ReaderPassword)', CHECK_POLICY = ON, DEFAULT_DATABASE = [$(HostDatabase)];
ELSE
    ALTER LOGIN [mode_reader] WITH PASSWORD = N'$(ReaderPassword)';
GO
USE [$(HostDatabase)];
GO
IF USER_ID(N'mode_reader') IS NULL
    CREATE USER [mode_reader] FOR LOGIN [mode_reader];
GO
GRANT SELECT ON dbo.Users TO [mode_reader];
DENY INSERT, UPDATE, DELETE, ALTER ON dbo.Users TO [mode_reader];
GO
