# Dependencies

All dependencies are open source and free for internal use. Licences were taken from package metadata where available; confirm them with legal before production.

## .NET (versions in [Directory.Packages.props](../Directory.Packages.props))

| Package | Version | Licence | Used by |
| --- | --- | --- | --- |
| .NET 10 runtime and ASP.NET Core (includes SignalR) | 10.0 | MIT | API, mock issuer |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.3 | PostgreSQL | Infrastructure |
| Microsoft.EntityFrameworkCore.Design | 10.0.14 | MIT | migrations (design-time only) |
| Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore | 10.0.14 | MIT | readiness check |
| Microsoft.Data.SqlClient | 7.1.1 | MIT | host user directory |
| System.Security.Cryptography.Pkcs (transitive pin) | 10.0.14 | MIT | overrides vulnerable 9.0.18 from SqlClient |
| Microsoft.IdentityModel.JsonWebTokens | 8.23.0 | MIT | mock issuer |
| Microsoft.AspNetCore.Authentication.JwtBearer | 10.0.14 | MIT | host token validation |
| Markdig | 1.5.1 | BSD-2-Clause | parsing message Markdown for sanitizing and mentions |
| Lib.Net.Http.WebPush | 3.3.1 | MIT | Web Push (VAPID, payload encryption) |
| MailKit | 4.18.2 | MIT | SMTP for email digests |
| Testcontainers.PostgreSql | 4.16.0 | MIT | tests: throwaway PostgreSQL (needs Docker on the build agent) |
| Microsoft.AspNetCore.SignalR.Client | 10.0.14 | MIT | tests: realtime connection tests |
| Serilog.AspNetCore | 10.0.0 | Apache 2.0 | structured logging |
| Swashbuckle.AspNetCore | 10.3.1 | MIT | OpenAPI (Development only) |
| xunit, xunit.runner.visualstudio | 2.9.3, 4.0.1 | Apache 2.0 | tests |
| Microsoft.NET.Test.Sdk, Microsoft.AspNetCore.Mvc.Testing | 18.11.0, 10.0.14 | MIT | tests |
| dotnet-ef (local tool) | 10.0.14 | MIT | migrations |

`dotnet list package --vulnerable --include-transitive` reported no vulnerable packages on 2026-11-01.

## Python file processor ([requirements.txt](../services/file-processor/requirements.txt))

| Package | Version | Licence |
| --- | --- | --- |
| fastapi | 0.162.0 | MIT |
| uvicorn | 0.54.0 | BSD-3-Clause |
| httpx | 0.28.1 | BSD-3-Clause |
| pillow | 13.0.0 | MIT-CMU (HPND) |
| pypdfium2 (bundles PDFium) | 5.14.0 | Apache-2.0 / BSD-3-Clause |
| pypdf | 6.21.4 | BSD-3-Clause |
| python-docx | 1.2.0 | MIT |
| openpyxl | 3.1.5 | MIT |
| python-pptx | 1.0.2 | MIT |
| pytest (dev) | 9.2.0 | MIT |

## Client ([client/package.json](../client/package.json))

| Package | Licence |
| --- | --- |
| react, react-dom 18 | MIT |
| vite, @vitejs/plugin-react, vitest | MIT |
| typescript | Apache 2.0 |
| jsdom, @testing-library/react | MIT |

`npm audit` reported 0 vulnerabilities on 2026-11-01.

## Container images ([docker-compose.yml](../docker-compose.yml))

| Image | Licence | Note |
| --- | --- | --- |
| postgres:16-alpine | PostgreSQL | |
| clamav/clamav:1.4 | GPL-2.0 | Used unmodified as a separate daemon; config in `docker/clamav/clamd.conf` |
| python:3.11-slim | PSF | file-processor base |
| mcr.microsoft.com/dotnet/aspnet:10.0, sdk:10.0 | MIT | |
| node:22-alpine | MIT | client dev server |

## Not adopted

- **FluentAssertions:** v8 requires a commercial licence.
- **Moq:** concerns about its SponsorLink telemetry; tests use real implementations or hand-written fakes.
- **MinIO:** images are no longer published (D10).
- **SeaweedFS, AWSSDK.S3:** not needed while files are stored on local disk (D26). Either can be added later behind `IFileStore`.
