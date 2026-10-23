<#
.SYNOPSIS
    Build / test / run / publish helper for HolyScreen.

.EXAMPLE
    .\build.ps1 build            # Debug build
    .\build.ps1 build -Release   # Release build
    .\build.ps1 test             # unit tests
    .\build.ps1 e2e              # end-to-end tests against the real app (moves the mouse, shows overlays)
    .\build.ps1 e2e -E2EArgs '--exe','dist\HolyScreen.exe'   # the same, against the published exe
    .\build.ps1 run              # build + launch (tray app)
    .\build.ps1 publish          # ONE standalone .\dist\HolyScreen.exe (runtime + WPF + assets inside, nothing else needed)
    .\build.ps1 publish -FrameworkDependent   # small exe that needs the .NET 10 Desktop Runtime installed
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('build', 'test', 'e2e', 'run', 'publish', 'clean')]
    [string]$Task = 'build',

    [switch]$Release,
    [switch]$FrameworkDependent,

    # Extra arguments passed to the E2E runner (e.g. -E2EArgs '--only','overlay').
    [string[]]$E2EArgs = @()
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'src\HolyScreen\HolyScreen.csproj'
$testProj = Join-Path $root 'tests\HolyScreen.Tests\HolyScreen.Tests.csproj'
$e2eProj = Join-Path $root 'tests\HolyScreen.E2E\HolyScreen.E2E.csproj'
$config = if ($Release -or $Task -eq 'publish') { 'Release' } else { 'Debug' }

# Prefer a system dotnet with a 10.x SDK; otherwise fall back to the per-user install.
function Resolve-Dotnet {
    $candidates = @(
        (Get-Command dotnet -ErrorAction SilentlyContinue).Source,
        (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe')
    )
    foreach ($c in $candidates) {
        if ($c -and (Test-Path $c)) {
            $sdks = & $c --list-sdks 2>$null
            if ($sdks -match '^10\.') { return $c }
        }
    }
    throw "No .NET 10 SDK found. Install it from https://dot.net (channel 10.0)."
}

function Invoke-Checked([scriptblock]$block) {
    # Native tools write progress to stderr; under 'Stop' Windows PowerShell would turn that into a terminating
    # error when output is redirected. Judge success by the exit code instead.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $block } finally { $ErrorActionPreference = $previous }
    if ($LASTEXITCODE -ne 0) { throw "Command failed with exit code $LASTEXITCODE." }
}

$dotnet = Resolve-Dotnet
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
Write-Host "Using dotnet: $dotnet ($config)" -ForegroundColor Cyan

switch ($Task) {
    'build' { Invoke-Checked { & $dotnet build $proj -c $config } }
    'test'  { Invoke-Checked { & $dotnet test $testProj -c $config } }
    'e2e'   {
        Invoke-Checked { & $dotnet build $e2eProj -c $config }
        $runner = Join-Path $root "tests\HolyScreen.E2E\bin\$config\net10.0-windows10.0.17763.0\HolyScreen.E2E.exe"
        Invoke-Checked { & $runner --config $config @E2EArgs }
    }
    'run'   { Invoke-Checked { & $dotnet run --project $proj -c $config } }
    'clean' {
        Get-ChildItem $root -Include bin, obj -Recurse -Directory | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item (Join-Path $root 'dist') -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host 'Cleaned bin/obj/dist.' -ForegroundColor Green
    }
    'publish' {
        $dist = Join-Path $root 'dist'
        Remove-Item $dist -Recurse -Force -ErrorAction SilentlyContinue
        # Single file in both modes; symbols are embedded so log stack traces keep line numbers without a .pdb.
        # No ReadyToRun of our own: the runtime pack is already precompiled, and R2R here builds a 116 MB composite
        # image of the whole framework (+45 MB, 2.5 min) for no measurable start-up gain.
        $publishArgs = @($proj, '-c', 'Release', '-r', 'win-x64', '-o', $dist,
                         '-p:PublishSingleFile=true', '-p:PublishReadyToRun=false', '-p:DebugType=embedded')
        if ($FrameworkDependent) {
            $publishArgs += @('--self-contained', 'false')
            Write-Host 'Publishing framework-dependent single file (requires the .NET 10 Desktop Runtime on target).' -ForegroundColor Yellow
        } else {
            # Compressed, and on first start the host unpacks everything once into a cache under
            # %TEMP%\.net\HolyScreen\<hash> (never beside the exe). Assemblies are then mapped from those files like a
            # normal install. Measured on this PC: 65 MB exe, 13 MB private memory at start-up. Without extraction,
            # a compressed bundle inflates every assembly into private memory (70-127 MB at start-up, for a tray
            # app that runs all day); uncompressed it would be a 200 MB exe.
            $publishArgs += @('--self-contained', 'true',
                              '-p:IncludeNativeLibrariesForSelfExtract=true',
                              '-p:IncludeAllContentForSelfExtract=true',
                              '-p:EnableCompressionInSingleFile=true')
            Write-Host 'Publishing ONE standalone exe (no runtime, no assets needed on the target PC).' -ForegroundColor Yellow
        }
        Invoke-Checked { & $dotnet publish @publishArgs }
        $files = Get-ChildItem $dist -File
        $files | ForEach-Object { Write-Host ("  {0,-24} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB)) }
        if ($files.Count -ne 1) { throw "Expected exactly one file in $dist, found $($files.Count)." }
        Write-Host "Output: $dist\HolyScreen.exe" -ForegroundColor Green
    }
}
