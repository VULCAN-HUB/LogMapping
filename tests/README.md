# LogMapping preflight tests

Requires .NET 8 SDK and Node.js on Windows. All fixture directories must be dedicated to synthetic tests. Never pass a real catalog/data directory.

From the repository root, in PowerShell:

```powershell
$fixture = Join-Path $env:TEMP ("LogMapping-regression-" + [guid]::NewGuid())
dotnet run --project tests/RegressionTests.csproj -c Release -- $fixture
node tests/ui_regression.cjs . (Join-Path $fixture 'viewer.html')

$large = Join-Path $env:TEMP ("LogMapping-scale-" + [guid]::NewGuid())
dotnet run --project tests/Preflight/Preflight.csproj -c Release -- $large 6400000 40

$scan = Join-Path $env:TEMP ("LogMapping-scanner-" + [guid]::NewGuid())
$tree = Join-Path $scan 'tree'
New-Item -ItemType Directory -Path $tree
New-Item -ItemType Junction -Path (Join-Path $tree 'loop') -Target $tree
dotnet run --project tests/ScannerTests/ScannerTests.csproj -c Release -- $scan

dotnet build LogMapping/LogMapping.csproj -c Release
dotnet run --project tests/PlatformProbe/PlatformProbe.csproj -c Release -- LogMapping/bin/Release/net8.0-windows/win-x64/LogMapping.dll LogMapping/resources/index.html
```

- RegressionTests: SQLite scope, migration, backup, atomic save, cancellation, synthetic APFS, auto-backup failure and handle cleanup.
- ui_regression.cjs: mock DOM/native transport, save races, catalog switches, filter/ordering, tagging and generated HTML viewer search cancellation.
- Preflight: 40 logical drive records and 6,400,000 generated file metadata rows, not 40 physical drives or 6.4M filesystem files. Includes an owned child-process termination, SQLite FULL fault injection, locked/readonly files, concurrency, CSV/HTML export, relocation/restore, integrity check and timing/peak process working set. Requires several GB of temporary disk space. Use a fresh directory for every full run. Test data is retained for inspection.
- ScannerTests: creates 30,000 actual small files, detects a pre-created junction loop, exercises Unicode paths beyond 260 characters and a disappearing folder.
- PlatformProbe: verifies installed WebView2 Runtime, process architecture, embedded HTML hash and compiled WPF resources without automating desktop UI.

For final viewer-only regeneration from an existing synthetic scale fixture:

```powershell
dotnet run --project tests/Preflight/Preflight.csproj -c Release -- --export-existing $large (Join-Path $large 'viewer-final.html')
```

These tests do not replace native WPF window interactions, UAC/file-dialog/DPI tests or physical APFS/HFS/removable-drive checks. Desktop automation is unavailable in the current agent environment; browser tests cover the actual HTML and generated viewer using synthetic data.

Additional checks for the 40-drive / 6.4M fixture (after the full scale run):

```powershell
dotnet run --project tests/Preflight/Preflight.csproj -c Release -- --backup-policy $large
dotnet run --project tests/Preflight/Preflight.csproj -c Release -- --migration $large
```

Migration makes a separate DB copy, constructs a legacy name-only FTS schema and rebuilds the current path index. It requires several additional GB and explicitly verifies 40 drives and 6,400,000 file rows. Its `migration.db` destination must not already exist.
