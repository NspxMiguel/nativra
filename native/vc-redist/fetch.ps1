<#
  Fetches Microsoft's own x86 Visual C++ redistributables (2010, 2012 and 2013)
  and extracts the C and C++ runtime DLLs games link against (msvcr/msvcp/vcomp/
  vcamp 100, 110 and 120) into -Out, where the app package carries them as x86\
  and the x86 layer maps them when a game does not bring its own copy.

  Microsoft permits redistributing these runtime DLLs with applications. They are
  downloaded at build time and packaged only; they are never committed to git.

  Every installer must carry a valid Microsoft Authenticode signature, the 2010
  and 2012 installers (fixed URLs) are also pinned by SHA-256, and every
  extracted DLL must be signed by Microsoft too, or the script throws.

  The 2015-2022 runtime (vcruntime140, msvcp140...) and ucrtbase come from the
  build machine's Visual Studio and Windows SDK redistributable folders instead
  (see build-uwp.yml); -Verify checks their signatures with the same rule.
#>
param(
    [Parameter(Mandatory)] [string] $Out,
    [string] $SevenZip = "7z",
    # Off Windows there is no Authenticode check to run (local trial of the extraction only).
    [switch] $NoSignatureCheck
)
$ErrorActionPreference = "Stop"

$redists = @(
    @{ Year = "2010"; Suffix = "100"; Sha256 = "99dce3c841cc6028560830f7866c9ce2928c98cf3256892ef8e6cf755147b0d8"
       Url = "https://download.microsoft.com/download/1/6/5/165255E7-1014-4D0A-B094-B6A430A6BFFC/vcredist_x86.exe" },
    @{ Year = "2012"; Suffix = "110"; Sha256 = "b924ad8062eaf4e70437c8be50fa612162795ff0839479546ce907ffa8d6e386"
       Url = "https://download.microsoft.com/download/1/6/B/16B06F60-3B20-4FF2-B699-5E9B7962F9AE/VSU_4/vcredist_x86.exe" },
    @{ Year = "2013"; Suffix = "120"; Sha256 = $null   # aka.ms follows Microsoft's latest 2013 update
       Url = "https://aka.ms/highdpimfc2013x86enu" }
)

function Assert-Microsoft([string] $path) {
    if ($NoSignatureCheck) { return }
    $sig = Get-AuthenticodeSignature -FilePath $path
    if ($sig.Status -ne "Valid" -or $sig.SignerCertificate.Subject -notmatch "O=Microsoft Corporation") {
        throw "$path is not validly signed by Microsoft Corporation (status $($sig.Status))"
    }
}

# Unpacks every cabinet found under $dir, a few levels deep: a Burn bundle holds a
# cabinet of MSI payloads, which hold cabinets of the DLLs.
function Expand-Cabinets([string] $dir) {
    for ($pass = 0; $pass -lt 4; $pass++) {
        $found = $false
        foreach ($file in Get-ChildItem $dir -Recurse -File) {
            $head = [byte[]](Get-Content -Path $file.FullName -AsByteStream -TotalCount 4)
            if ($head.Length -eq 4 -and [System.Text.Encoding]::ASCII.GetString($head) -eq "MSCF") {
                $target = "$($file.FullName).d"
                if (Test-Path $target) { continue }
                & $SevenZip x -y "-o$target" $file.FullName | Out-Null
                $found = $true
            }
        }
        if (-not $found) { break }
    }
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("vcredist-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $work, $Out | Out-Null
foreach ($r in $redists) {
    $installer = Join-Path $work "vcredist_$($r.Year).exe"
    Write-Host "downloading Visual C++ $($r.Year) x86 redistributable"
    Invoke-WebRequest -Uri $r.Url -OutFile $installer -MaximumRetryCount 3 -RetryIntervalSec 5
    $hash = (Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($r.Sha256 -and $hash -ne $r.Sha256) { throw "vcredist $($r.Year): SHA-256 $hash differs from the pinned $($r.Sha256)" }
    Assert-Microsoft $installer

    $dir = Join-Path $work $r.Year
    # Burn bundles keep their payload in an attached container (-t#); the 2010 setup is a plain
    # self-extractor.
    & $SevenZip x -y "-o$dir" $installer | Out-Null
    if ($r.Year -ne "2010") { & $SevenZip x -y -t# "-o$(Join-Path $dir attached)" $installer | Out-Null }
    Expand-Cabinets $dir

    # The MSI file table names the DLLs F_CENTRAL_<name>_x86 inside the cabinets.
    $pattern = "^F_CENTRAL_(msvcr|msvcp|vcomp|vcamp)$($r.Suffix)_x86$"
    $dlls = Get-ChildItem $dir -Recurse -File | Where-Object { $_.Name -match $pattern }
    foreach ($need in "msvcr", "msvcp") {
        if (-not ($dlls | Where-Object { $_.Name -match "^F_CENTRAL_$need" })) { throw "$need$($r.Suffix) not found in the $($r.Year) redistributable" }
    }
    foreach ($dll in $dlls) {
        $name = ($dll.Name -replace "^F_CENTRAL_", "" -replace "_x86$", "") + ".dll"
        $dest = Join-Path $Out $name
        Copy-Item $dll.FullName $dest -Force
        Assert-Microsoft $dest
        Write-Host ("  {0} ({1} bytes)" -f $name, (Get-Item $dest).Length)
    }
}
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue

# The 2015-2022 runtime and the UCRT the package already carries from the build machine.
foreach ($dll in Get-ChildItem $Out -File -Include "vcruntime140*.dll", "msvcp140*.dll", "concrt140.dll", "ucrtbase.dll" -Recurse) {
    Assert-Microsoft $dll.FullName
}
