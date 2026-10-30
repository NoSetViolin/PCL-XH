# CurseForge resource integration

The launcher supports All, CurseForge and Modrinth in each resource page's source
filter. Official CurseForge requests use HTTPS and `x-api-key`; the key is never
attached to Modrinth, CDN requests or the retired mirror.

Set `PCL_CURSEFORGE_API_KEY` privately before building. `PCL_WRITE_SECRET=1`
embeds it through the existing EnvironmentInterop source generator for distributable
builds. Use `-p:UseSharedCompilation=false` so the compiler inherits this environment.
Without embedding, the launcher also reads `PCL_CURSEFORGE_API_KEY` at runtime.
Never put the key in tracked source, command-line properties, build logs or reports.

For the optional integration harness, build using `Inject.targets` as described in
README.md. Create `resources-only.flag` beside the diagnostic executable and set
`PCL_FRAME_QA_ROOT` to a fresh isolated QA directory before launching it.
The pass performs live metadata searches and downloads one small AppleSkin fixture
through the production downloader, then verifies its hash. It does not install or
execute the downloaded mod. Read `resources-report.txt` (or `report.txt` on failure)
and inspect the two source-selector screenshots.

Implementation reference: PCL-Community/PCL-CE `dev`,
`Plain Craft Launcher 2/Modules/Minecraft/ModComp.cs` and `ModLocalComp.cs`, retrieved 2026-10-30.
File lists use 50-item pages instead of the upstream oversized single-page request.
Installed-resource scans query both providers: SHA1 matching on Modrinth and exact
MurmurHash2 fingerprint matching on CurseForge. CurseForge project indexes identify
candidate releases; file metadata is checked against the instance Minecraft version
and loader before offering an update. Quilt instances accept matching Fabric releases.
Both sources load into independent snapshots and merge after completion, preserving
Modrinth project metadata where available. Equivalent hashes combine CDN URLs and
changelogs; distinct releases select the newest compatible file. A failed provider
keeps the other provider's results and marks the cache incomplete for retry.
Cache schema 9 invalidates the older Modrinth-only six-hour cache.

Create `local-updates-only.flag` instead of `resources-only.flag` for installed-mod
update validation. It exercises deterministic merge/failure/compatibility/cancellation
fixtures, cached metadata round trips, live AppleSkin Forge 1.20.1 fingerprint matching
from 2.5.0 to 2.5.1, disabled-file recognition and a real update download with hash
verification. Results are written to `local-updates-report.txt`. Fixtures stay in the
isolated QA root; no real instance files are replaced or executed.

Sources:
- https://github.com/PCL-Community/PCL-CE/blob/dev/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModComp.cs
- https://docs.curseforge.com/rest-api/
