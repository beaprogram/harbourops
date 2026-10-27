# Third-party notices

HarbourOps uses open-source components under permissive licences. This file records what is distributed where and how each licence's notice requirement is met. Licence texts are not copied here, because they ship with the components themselves. It was reviewed on 2026-10-26 from each package's own metadata.

**The repository's own licence** has not been chosen yet. Until a `LICENSE` file is added, no licence is granted for this project's own code.

## What this repository contains

Only HarbourOps' own source, documentation, and screenshots:

- **Third-party code:** none is vendored. Dependencies are restored from npm and NuGet at the exact versions in the committed lockfiles.
- **Screenshots:** they show this app with fictional data.
- **Favicon:** an original SVG.
- **Fonts and images:** no third-party fonts or images. The interface uses the visitor's system fonts.

## Distributed to visitors of the hosted demo (the web app's JavaScript and CSS)

The production build bundles these packages. Vite writes their licence texts to **`/third-party-licenses.txt`**, which is served next to the app (`frontend/vite.config.ts`, `build.license`).

| Package | Licence |
| --- | --- |
| react, react-dom, scheduler | MIT |
| react-router | MIT |
| @tanstack/react-query, @tanstack/query-core | MIT |
| react-hook-form, @hookform/resolvers | MIT |
| zod | MIT |
| tailwindcss (generated CSS) | MIT. Its licence header is kept in the CSS file |

## Inside the container image (pushed only to a private registry, not published)

| Component | Licence | Notice |
| --- | --- | --- |
| .NET 10 runtime, ASP.NET Core, EF Core and other `Microsoft.*` packages | MIT | Package metadata. Base image `mcr.microsoft.com/dotnet/aspnet:10.0.13-noble-chiseled-extra` (Microsoft's image terms) |
| Npgsql.EntityFrameworkCore.PostgreSQL (and Npgsql) | PostgreSQL License | Package metadata |
| SkiaSharp, SkiaSharp.NativeAssets.Linux.NoDependencies | MIT. Bundled Skia and codec code is BSD-3-Clause and similar | `/app/licenses/SkiaSharp-LICENSE.txt`, `/app/licenses/SkiaSharp-THIRD-PARTY-NOTICES.txt` |
| Azure.Storage.Blobs, Azure.Identity, Azure.Core, Azure.Extensions.AspNetCore.DataProtection.Keys, Azure.Security.KeyVault.Keys, Microsoft.Identity.Client | MIT | Package metadata |
| Apache.Arrow (dependency of Azure.Storage.Blobs) | Apache-2.0 (the package has no NOTICE file) | `/app/licenses/Apache.Arrow-LICENSE.txt` |
| Scalar.AspNetCore (API reference, Development only) | MIT | Package metadata |

## Development and test tools (not distributed)

| Tool | Licence |
| --- | --- |
| xunit.v3 | Apache-2.0 |
| Testcontainers (.NET), Microsoft.AspNetCore.Mvc.Testing, Microsoft.Extensions.TimeProvider.Testing | MIT |
| TypeScript | Apache-2.0 |
| @playwright/test | Apache-2.0 |
| @axe-core/playwright | MPL-2.0, used unmodified. MPL obligations apply only to modified MPL files |
| Vite, Vitest, ESLint, Testing Library, jsdom, and the other frontend dev dependencies | MIT |
| Container images used in tests: `postgres:18.6-alpine` (PostgreSQL License) and `mcr.microsoft.com/azure-storage/azurite:3.37.0` (MIT) | As stated |

## Keeping this current

When adding a dependency:
1. Check its licence (see [CLAUDE.md](CLAUDE.md)).
2. If it is bundled into the frontend, check that it appears in the generated `third-party-licenses.txt`.
3. If it ships native code or a NOTICE file in the container, copy that file into `/app/licenses` in the `Dockerfile`.
