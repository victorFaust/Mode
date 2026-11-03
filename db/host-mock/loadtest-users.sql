-- Load-test people for the local mock host directory (development only).
-- sqlcmd -S . -E -d ModeHostMock -v UserCount=1000 -i db/host-mock/loadtest-users.sql
-- Ids match tools/Mode.LoadTest: 0a1b0002-0000-4000-8000-000000000001 and up.
SET NOCOUNT ON;
WITH numbers AS (
    SELECT TOP ($(UserCount)) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
MERGE dbo.Users AS target
USING (
    SELECT CAST('0a1b0002-0000-4000-8000-' + RIGHT('000000000000' + CAST(n AS varchar(12)), 12) AS uniqueidentifier) AS Id,
           N'Load User ' + CAST(n AS nvarchar(12)) AS DisplayName,
           N'load' + CAST(n AS nvarchar(12)) + N'@loadtest.example' AS Email
    FROM numbers
) AS source
ON target.Id = source.Id
WHEN NOT MATCHED THEN INSERT (Id, DisplayName, Email, IsActive) VALUES (source.Id, source.DisplayName, source.Email, 1);
SELECT COUNT(*) AS LoadUsers FROM dbo.Users WHERE Email LIKE N'%@loadtest.example';
