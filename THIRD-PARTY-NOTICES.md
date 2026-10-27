# Third-party notices

HarbourOps' own code is released under the [MIT License](LICENSE), Copyright (c) 2026 Arup Halder. It uses open-source components under their own licences. This file records what is distributed where and how each licence's notice requirement is met. Licence texts are not copied here, because they ship with the components themselves. It was reviewed on 2026-10-27 from each package's own metadata and bundled licence files (the npm lockfiles and installed packages, and each NuGet package's `.nuspec`).

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
| .NET 10 runtime, ASP.NET Core, EF Core and other `Microsoft.*` and `System.*` packages | MIT | Package metadata. Base image `mcr.microsoft.com/dotnet/aspnet:10.0.13-noble-chiseled-extra` (Microsoft's image terms) |
| Npgsql.EntityFrameworkCore.PostgreSQL (and Npgsql) | PostgreSQL License | Package metadata |
| SkiaSharp, SkiaSharp.NativeAssets.Linux.NoDependencies | MIT. Bundled Skia and codec code is BSD-3-Clause and similar | `/app/licenses/SkiaSharp-LICENSE.txt`, `/app/licenses/SkiaSharp-THIRD-PARTY-NOTICES.txt` |
| Azure.Storage.Blobs, Azure.Identity, Azure.Core, Azure.Extensions.AspNetCore.DataProtection.Keys, Azure.Security.KeyVault.Keys, Microsoft.Identity.Client | MIT | Package metadata |
| Apache.Arrow (dependency of Azure.Storage.Blobs) | Apache-2.0 (the package has no NOTICE file) | `/app/licenses/Apache.Arrow-LICENSE.txt` |
| Scalar.AspNetCore (API reference, Development only) | MIT | Package metadata |

## Development, build, and test tools

These run on a developer's machine or a CI runner. npm and NuGet download them at the locked versions into git-ignored folders. None is committed to this repository, copied into the container image (`.dockerignore` excludes `e2e/` and every `node_modules`), or bundled into the web app. The one exception, CI failure reports, is covered below.

| Tool | Licence |
| --- | --- |
| xunit.v3 | Apache-2.0 |
| Testcontainers (.NET), Microsoft.AspNetCore.Mvc.Testing, Microsoft.Extensions.TimeProvider.Testing | MIT |
| TypeScript | Apache-2.0 |
| @playwright/test, playwright, playwright-core 1.64.1 | Apache-2.0 |
| @axe-core/playwright 4.14.0 and axe-core 4.14.0 (accessibility scans) | MPL-2.0, see below |
| lightningcss 1.32.0 and 1.33.0 (CSS transformer that Vite and Tailwind run during the build) | MPL-2.0, see below |
| caniuse-lite (browser-support data that Babel's browserslist reads inside the ESLint React Hooks plugin) | CC-BY-4.0. Read during linting; none of it is in any build output |
| Vite, Vitest, ESLint, Testing Library, jsdom, and the other frontend dev dependencies | MIT, ISC, BSD-2-Clause, BSD-3-Clause, Apache-2.0, BlueOak-1.0.0, CC0-1.0, or MIT-0 |
| Container images used in tests: `postgres:18.6-alpine` (PostgreSQL License) and `mcr.microsoft.com/azure-storage/azurite:3.37.0` (MIT) | As stated |

### The MPL-2.0 components

MPL-2.0 is a **file-level copyleft** licence. Its conditions attach to the covered files themselves, modified or not. Whoever distributes them must:

- keep them under MPL-2.0 with their notices intact;
- tell recipients the licence applies;
- make their source code available.

Changes to those files must also be released under MPL-2.0. The licence does not reach separate files that are combined with them (a "Larger Work"), so it places no conditions on HarbourOps' own code. HarbourOps uses all of these components unmodified.

How each one actually reaches other people (checked on 2026-10-27):

- **axe-core and @axe-core/playwright** are installed by npm from the registry into `e2e/node_modules`. During a scan, @axe-core/playwright injects axe-core's engine into the browser under test as the text of its unminified `axe.js` function. That text does **not** include the file's licence header.
- **CI failure reports distribute it.** When an end-to-end test fails, CI uploads the Playwright report (kept 7 days). Because this repository is public, anyone signed in to GitHub can download that report. Its traces record the injected engine: a traced local axe scan held four 1.48 MB copies of it and no licence notice. So before uploading, `ci.yml` adds a `third-party-licenses/` folder to the report containing:
  - axe-core's `LICENSE` and `LICENSE-3RD-PARTY.txt`;
  - a README naming the version and its source (`https://github.com/dequelabs/axe-core/tree/v<version>`);
  - Playwright's `LICENSE`, `NOTICE`, and `ThirdPartyNotices.txt`, because the report's viewer is Playwright code.

  Passing runs upload nothing.
- **lightningcss** runs only during `npm run build`. Its output is HarbourOps' own CSS, which contains none of its code, so nothing of it is distributed.

## Keeping this current

When adding a dependency:
1. Check its licence (see [CLAUDE.md](CLAUDE.md)).
2. If it is bundled into the frontend, check that it appears in the generated `third-party-licenses.txt`.
3. If it ships native code or a NOTICE file in the container, copy that file into `/app/licenses` in the `Dockerfile`.
4. If its code can end up in a CI artifact (as the injected axe-core engine does in Playwright traces), add its licence to the notice step in `ci.yml`.
