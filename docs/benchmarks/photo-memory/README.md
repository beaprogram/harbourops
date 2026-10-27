# Benchmark: memory during photo uploads (milestone 4)

This benchmark answers two questions for the hosted demo:

1. How much memory does the app container use while photos at the configured limits are uploaded, alone and alongside other requests?
2. Is that usage bounded, or does it grow with every round of uploads?

The numbers are **single runs on one laptop**. They show the order of magnitude and the effect of each setting, not a guarantee for Azure hardware.

## What was found

- **Native memory ratcheted up with the default glibc allocator.** Repeated rounds of the same uploads grew the container from 750 MiB (one round) to 1,307 MiB (five rounds). SkiaSharp frees its multi-megabyte pixel buffers correctly, but glibc keeps freed memory in per-thread arenas.
- **Two allocator settings fixed it** (now in the `Dockerfile`):
  - `MALLOC_ARENA_MAX=2`: at most two arenas.
  - `MALLOC_MMAP_THRESHOLD_=1048576`: buffers of 1 MiB or more are returned to the OS when freed.

  With both, eight rounds peaked at 715 MiB, and memory fell back to 84–150 MiB after every round.
- **GC settings did not help.** Workstation GC (1,271 MiB) and GC conserve-memory mode (1,211 MiB) were no better than the default, because the growth was native memory, not the managed heap.
- **Waiting uploads are now bounded.** At most `MaxConcurrentProcessing + MaxQueuedUploads` uploads (2 + 4 by default) may hold a buffered file. Further uploads get 503 `uploads_busy` before their body is read.
- **The hosted demo processes one photo at a time.** `appsettings.PublicDemo.json` sets `MaxConcurrentProcessing: 1`. That run peaked at 496 MiB over eight rounds, against 1.75 GB on an App Service B1. It costs nothing in throughput on a 1-vCPU plan.

## Environment

| | |
| --- | --- |
| Machine | Apple M4, 10 cores, 16 GiB RAM, macOS 27.0.1 |
| Container runtime | Colima (Lima VM, aarch64, 4 vCPU, 8 GiB, cgroup v2), Docker Engine 29.5.2 |
| App container limits | **1 vCPU, 1,750 MiB**, chosen to mirror an App Service B1 (1 core, 1.75 GB) |
| Image | Built from this repository's `Dockerfile` (`aspnet:10.0.13-noble-chiseled-extra`, linux/arm64), `ASPNETCORE_ENVIRONMENT=Demo` |
| Database | `postgres:18.6-alpine` in the same throwaway Compose project (`compose.yml`) |
| Date | 2026-10-26 |

## Workload (`harness.mjs`)

The harness drives the real HTTP API with cookies and antiforgery tokens, like the browser.

**Input files** (`make_images.py`):

| File | Size | Why |
| --- | --- | --- |
| 8000 × 5000 PNG (40 MP) | 0.12 MiB | The most expensive decode: about 160 MB of pixels |
| 10000 × 4000 PNG (40 MP) | 0.12 MiB | At the longest-edge limit |
| 8000 × 5000 JPEG | 4.79 MiB | Close to the 5 MiB body limit |
| 4032 × 3024 JPEG | 2.99 MiB | A typical phone photo |

**Scenarios:**

- **`burst`.** 12 uploads at once, cycling through the three worst-case files.
- **`sustained`.** 6 uploaders × 3 uploads each, cycling through all four files.
- **Concurrent activity.** In both scenarios, two readers loop over the list, dashboard, CSV export, and photo previews while uploads run.

**Measurements** (`measure.sh`):

- **Fresh container per run.** The container is recreated before each run, so cgroup v2 `memory.peak` covers only that run.
- **Sampling.** Every 200 ms the script records `memory.current` and the `anon` line of `memory.stat`.
- **After the run.** It records `memory.peak`, the OOM counters in `memory.events`, the anonymous memory after each round, and the process's peak resident set (`VmHWM`).

## Results (1 vCPU, 1,750 MiB; arm64 native)

### Before the allocator fix (default glibc settings)

| Run | Uploads | Outcome | `memory.peak` |
| --- | --- | --- | --- |
| Burst, queue bound 4 | 12 | 6 created, 6 refused with `uploads_busy` | 518 MiB |
| Burst, queue bound effectively off (1000) | 12 | 12 created | 634 MiB |
| Sustained, 1 round | 18 | 18 created | 750 MiB |
| Sustained, 3 rounds | 54 | all created | 1,010 MiB |
| Sustained, 3 rounds, **1,024 MiB limit** | 54 | all created | 889 MiB (87% of the limit) |
| Sustained, 3 rounds, 1,024 MiB, 1 decode at a time | 54 | 15 of 18 created per round, 3 `uploads_busy` | 708 MiB |

### Allocator and GC variants (5 rounds of `sustained`)

| Setting | `memory.peak` | Anonymous memory after each round (MiB) |
| --- | --- | --- |
| Defaults | 1,307 MiB | 340, 508, 700, 777, 708 |
| `MALLOC_ARENA_MAX=2` | 786 MiB | 183, 242, 264, 333, 329 |
| Workstation GC (`DOTNET_gcServer=0`) | 1,271 MiB | 288, 451, 558, 539, 822 |
| `DOTNET_GCConserveMemory=5` | 1,211 MiB | 353, 526, 642, 692, 657 |

**8 rounds:**

| Setting | `memory.peak` | After each round (MiB) |
| --- | --- | --- |
| `MALLOC_ARENA_MAX=2` | 797 MiB | 193, 166, 213, 260, 231, 245, 281, 271 |
| `MALLOC_MMAP_THRESHOLD_=1048576` | 790 MiB | 107, 152, 190, 233, 114, 127, 199, 209 |
| **Both** | **720 MiB** | 114, 92, 95, 112, 105, 137, 98, 117 |

### Final image (both settings baked into the `Dockerfile` ENV)

| Run | Outcome | `memory.peak` | After each round (MiB) |
| --- | --- | --- | --- |
| Idle after start-up | | 65 MiB (current) | |
| Sustained, 8 rounds, 2 decodes | 144 created | **715 MiB** | 84, 123, 97, 150, 115, 124, 132, 125 |
| Burst, 2 decodes | 7 created, 5 `uploads_busy` | 419 MiB | 76 |
| Sustained, 8 rounds, **1 decode** (hosted setting) | 15 created + 3 `uploads_busy` per round | **496 MiB** | 161, 194, 98, 95, 117, 138, 118, 109 |

No run recorded an OOM event, and no reader request failed.

**Latency cost.** With both allocator settings, the slowest upload in a sustained round took 1.8–2.2 s, against 1.5–1.6 s with the defaults. That is the price of returning large buffers to the OS.

## Linux x64 (amd64) runtime: functional check only

This Mac has no x64 hardware, so the x64 build was checked under QEMU user-mode emulation.

**The image.** The identical `/app` publish output went on the `linux/amd64` manifest of the same `aspnet:10.0.13-noble-chiseled-extra` base. .NET reported `RID: linux-x64`, and the process loaded `/app/runtimes/linux-x64/native/libSkiaSharp.so`. `DOTNET_EnableWriteXorExecute=0` was needed; that's a QEMU limitation, not a production setting.

**Results:**

| Check | Result |
| --- | --- |
| Smoke checks (orientation, EXIF/GPS/comment removal, 2,560 px stored size, 480 px preview, PNG flattening, a disguised HTML file, delivery headers, cross-organization 404) | 17 of 17 passed |
| Sustained load, 3 rounds | 54 of 54 created |
| Burst | 6 created, 6 `uploads_busy` |

**Not representative:** memory (961 MiB peak) and timing (12–53 s per upload) include the emulator and must not be used for sizing. **Native x64 verification** is the CI `e2e` job: the image runs as `linux/amd64` on an x64 runner, and `photo-processing.spec.ts` checks orientation and metadata removal there. That job has **not run yet**.

## Reproducing

```bash
cd docs/benchmarks/photo-memory
python3 -I make_images.py /tmp/harbourops-photo-memory/img          # macOS (uses sips)
(cd ../../../e2e && npm ci)                                           # the harness uses e2e's Playwright
HM_ARCH=arm64 docker compose -f compose.yml up -d --build             # throwaway project "hm-arm64"
HM_ARCH=arm64 HM_PORT=8099 HM_CPUS=1 HM_MEM=1750m HM_PROC=2 HM_REPEAT=8 ./measure.sh final-x8 sustained
HM_ARCH=arm64 HM_PORT=8099 HM_ENVFILE=env/arena2.env HM_REPEAT=5 ./measure.sh arena2-x5 sustained   # a variant
HM_ARCH=arm64 docker compose -f compose.yml down -v                   # remove the throwaway stack
```

`measure.sh` reads cgroup files through `colima ssh`. On a Linux Docker host, run the same commands without that prefix.

**`env/` variants.** These files only override variables of the *measurement* container. The final settings are the ones in the `Dockerfile`, so the default run (`env/default.env`) measures the shipped image.
