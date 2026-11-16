# Builds the two self-contained exes (WhiteHoney.exe: console, WhiteHoneyUI.exe: window) and the Nexus/Vortex-ready zip (game-root layout).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$version = ([xml](Get-Content "$root/src/WhiteHoney/WhiteHoney.csproj")).Project.PropertyGroup.Version | Select-Object -First 1
dotnet publish "$root/src/WhiteHoney" -c Release -o "$root/dist/bin" -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
dotnet publish "$root/src/WhiteHoneyUI" -c Release -o "$root/dist/bin-ui" -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (window) failed" }
$stage = "$root/dist/stage"
Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
$tool = New-Item -ItemType Directory -Force "$stage/tools/White Honey"
Copy-Item "$root/dist/bin/WhiteHoney.exe", "$root/dist/bin-ui/WhiteHoneyUI.exe", "$root/README.md", "$root/LICENSE" $tool
Copy-Item -Recurse "$root/package/*" $tool  # double-click shortcuts + START HERE.txt
$zip = "$root/dist/White-Honey-VRAM-Optimizer-$version.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$z = [System.IO.Compression.ZipFile]::Open($zip, "Create")
foreach ($f in Get-ChildItem -Recurse -File $stage) {
    $entry = $f.FullName.Substring((Resolve-Path $stage).Path.Length + 1).Replace("\", "/")  # forward slashes for Vortex/MO2
    [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, $f.FullName, $entry, "Optimal")
}
$z.Dispose()
Write-Host "Built $zip"
