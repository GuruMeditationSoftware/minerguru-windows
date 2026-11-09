# Runs in the VM: publishes both architectures, checks for personal traces and builds the installers.
# The trace patterns come from the Mac (scripts/release.sh copies them over): one regex per line, kept outside the
# repository because they are personal themselves. Patterns for names start at a word boundary where German UI
# text would match ("Entwicklung").
param([Parameter(Mandatory)][string]$Version, [Parameter(Mandatory)][string]$TracesFile)
$ErrorActionPreference = 'Stop'
$traceFile = (Resolve-Path $TracesFile).Path
$personal = (Get-Content $traceFile | Where-Object { $_.Trim() -and -not $_.StartsWith('#') }) -join '|'
if (-not $personal) { throw "no trace patterns in $TracesFile" }
Set-Location "$PSScriptRoot\..\.."
$iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" | Where-Object { Test-Path $_ } | Select-Object -First 1
$out = "src\MinerGuru\bin\release"
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
# Always from scratch: an incremental publish reuses binaries from a build with symbols, and those carry the pdb path.
Get-ChildItem src\MinerGuru, src\MinerGuru.Core -Directory | Where-Object Name -in 'bin', 'obj' |
    Get-ChildItem -Recurse -Directory -Filter Release | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
foreach ($arch in 'x64', 'arm64') {
    $platform = if ($arch -eq 'arm64') { 'ARM64' } else { 'x64' }
    $publish = (New-Item -ItemType Directory -Force "$out\$arch").FullName
    # No symbols: the pdbs and the paths to them would carry this machine's user folder.
    dotnet publish src\MinerGuru\MinerGuru.csproj -c Release -r "win-$arch" --self-contained true -p:Platform=$platform `
        -p:DebugType=none -p:DebugSymbols=false -p:IncludeSourceRevisionInInformationalVersion=false `
        -o $publish -nologo -v q 2>&1 | Where-Object { $_ -match ' error ' }
    if ($LASTEXITCODE) { throw "publish $arch failed" }
    foreach ($needed in 'coreclr.dll', 'MinerGuru.pri', 'App.xbf', 'Assets\AppIcon.ico', 'Assets\LICENSE.txt', 'Assets\THIRD-PARTY-NOTICES.txt') {
        if (-not (Test-Path "$publish\$needed")) { throw "$arch lacks $needed" }
    }
    $traces = @(Get-ChildItem $publish -Recurse -File | Where-Object { $_.Name -like 'MinerGuru*' -or $_.Extension -in '.pdb', '.json', '.xml', '.txt' } |
        Select-String -Pattern "$personal|192\.168\." -List -Encoding Default)
    $traces += Get-ChildItem $publish -Recurse -File | Where-Object { $_.Name -like 'MinerGuru*' } |
        Select-String -Pattern $personal -List -Encoding Unicode
    if ($traces) { $traces | ForEach-Object { $_.Path }; throw "personal traces in $arch" }
    # The Microsoft packages' own terms and notices ship with the app. Copied after the check: not our text.
    $licenses = (New-Item -ItemType Directory -Force "$publish\Licenses").FullName
    $packages = "$env:USERPROFILE\.nuget\packages"
    foreach ($file in @(
            @('microsoft.windowsappsdk', 'license.txt', 'WindowsAppSDK-license.txt'),
            @('microsoft.windowsappsdk', 'NOTICE.txt', 'WindowsAppSDK-NOTICE.txt'),
            @('microsoft.web.webview2', 'LICENSE.txt', 'WebView2-license.txt'),
            @('microsoft.web.webview2', 'NOTICE.txt', 'WebView2-NOTICE.txt'),
            @('system.security.cryptography.protecteddata', 'THIRD-PARTY-NOTICES.TXT', 'dotnet-THIRD-PARTY-NOTICES.txt'))) {
        $package = Get-ChildItem "$packages\$($file[0])" -Directory | Sort-Object { [version]$_.Name } | Select-Object -Last 1
        Copy-Item (Join-Path $package.FullName $file[1]) (Join-Path $licenses $file[2])
    }
    & $iscc /Q "/DVersion=$Version" "/DArch=$arch" "/DSource=$publish" "/O$((Resolve-Path $out).Path)" installer\MinerGuru.iss
    if ($LASTEXITCODE) { throw "installer $arch failed" }
}
