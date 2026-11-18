<#
  Verifies a real Windows publish folder without Python or model conversion.
  The folder is copied into a Cyrillic path. Only the benchmark DLL is added;
  all Core, model and native dependencies are loaded from the published app.
#>
param(
  [Parameter(Mandatory = $true)][string]$PublishedDirectory,
  [string]$SamplesDirectory,
  [string]$ReportsDirectory,
  [switch]$SkipProbeBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $SamplesDirectory) { $SamplesDirectory = Join-Path $root 'docs\test-samples' }
if (-not $ReportsDirectory) { $ReportsDirectory = Join-Path $root 'artifacts\published-ocr-smoke' }
$published = (Resolve-Path $PublishedDirectory).Path
$samples = (Resolve-Path $SamplesDirectory).Path
New-Item -ItemType Directory -Force -Path $ReportsDirectory | Out-Null
$reports = (Resolve-Path $ReportsDirectory).Path

if (-not $SkipProbeBuild) {
  & dotnet build (Join-Path $root 'tools\PerformanceVerify\PerformanceVerify.csproj') -c Release
  if ($LASTEXITCODE -ne 0) { throw 'Published OCR probe did not build.' }
}
$probe = Join-Path $root 'artifacts\bin\PerformanceVerify\release\DocumentTextRecognition.Performance.dll'
if (-not (Test-Path -LiteralPath $probe -PathType Leaf)) { throw "Missing probe DLL: $probe" }

$work = Join-Path ([IO.Path]::GetTempPath()) ('Проверка распознавания ' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
  Get-ChildItem -LiteralPath $published -Force | Copy-Item -Destination $work -Recurse -Force
  $required = @(
    'DocumentTextRecognition.exe', 'DocumentTextRecognition.Core.dll',
    'DocumentTextRecognition.runtimeconfig.json', 'DocumentTextRecognition.deps.json',
    'Microsoft.ML.OnnxRuntime.dll', 'onnxruntime.dll',
    'ocr\onnx\det.onnx', 'ocr\onnx\rec.onnx', 'ocr\onnx\model-manifest.json',
    'THIRD-PARTY-NOTICES.txt', 'licenses\PaddleSharp-Apache-2.0.txt',
    'licenses\OnnxRuntime-MIT.txt', 'licenses\OnnxRuntime-ThirdPartyNotices.txt'
  )
  foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $work $relative) -PathType Leaf)) {
      throw "Published app is missing $relative"
    }
  }
  $manifest = Get-Content -LiteralPath (Join-Path $work 'ocr\onnx\model-manifest.json') -Raw | ConvertFrom-Json
  foreach ($model in $manifest.Models) {
    $path = Join-Path $work ('ocr\onnx\' + $model.Output)
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $model.Sha256) {
      throw "Published model checksum mismatch: $($model.Output)"
    }
  }
  $coreHash = (Get-FileHash -LiteralPath (Join-Path $work 'DocumentTextRecognition.Core.dll') -Algorithm SHA256).Hash
  $check = Start-Process -FilePath (Join-Path $work 'DocumentTextRecognition.exe') -ArgumentList '--check' -WorkingDirectory $work -Wait -PassThru -NoNewWindow
  if ($check.ExitCode -ne 0) { throw "Published app --check failed: $($check.ExitCode)" }

  # Do not copy the probe's Core or native DLLs. The app's dependency graph and
  # runtime configuration below make the probe exercise the published payload.
  $publishedProbe = Join-Path $work 'DocumentTextRecognition.Performance.dll'
  Copy-Item -LiteralPath $probe -Destination $publishedProbe
  $truth = Get-Content -LiteralPath (Join-Path $samples 'verification.json') -Raw | ConvertFrom-Json
  $modes = @(
    @{ Name = 'default'; Flags = @(); Onnx = $null; KnownLines = $null },
    @{ Name = 'native'; Flags = @('--onnx', 'false', '--known-lines', 'false'); Onnx = $false; KnownLines = $false },
    @{ Name = 'onnx'; Flags = @('--onnx', 'true', '--known-lines', 'false'); Onnx = $true; KnownLines = $false },
    @{ Name = 'onnx-known-lines'; Flags = @('--onnx', 'true', '--known-lines', 'true'); Onnx = $true; KnownLines = $true }
  )
  $summary = @()
  foreach ($mode in $modes) {
    $output = Join-Path $reports ($mode.Name + '.json')
    $flags = $mode.Flags
    & dotnet exec --runtimeconfig (Join-Path $work 'DocumentTextRecognition.runtimeconfig.json') --depsfile (Join-Path $work 'DocumentTextRecognition.deps.json') $publishedProbe $samples $output @flags
    if ($LASTEXITCODE -ne 0) { throw "Published OCR probe failed: $($mode.Name)" }
    $result = Get-Content -LiteralPath $output -Raw | ConvertFrom-Json
    if ($result.CoreSha256 -ne $coreHash) { throw 'Probe loaded a different Core assembly instead of the published file.' }
    if ($mode.Onnx -ne $null -and $result.Configuration.ResolvedUsesOnnx -ne $mode.Onnx) {
      throw "Wrong resolved backend in $($mode.Name)"
    }
    if ($mode.KnownLines -ne $null -and $result.Configuration.ResolvedKnownLines -ne $mode.KnownLines) {
      throw "Wrong resolved known-line mode in $($mode.Name)"
    }
    if ($result.Incorrect -ne 0) { throw "Published OCR accepted incorrect names: $($mode.Name)" }
    if ($result.Configuration.ResolvedKnownLines -eq $true -and ($result.Correct -ne 5 -or $result.Rejected -ne 0)) {
      throw "Known-line published mode must read all five tracked samples, including the parenthesized name: $($mode.Name)"
    }
    foreach ($sample in $truth.Samples | Where-Object { $_.RecognitionRequired }) {
      $row = @($result.Samples | Where-Object { $_.File -eq $sample.File })
      if ($row.Count -ne 1 -or $row[0].ActualName -ne $sample.ExpectedName) {
        throw "Published OCR failed required sample $($sample.File) in $($mode.Name)"
      }
    }
    $summary += @{ Mode = $mode.Name; Configuration = $result.Configuration; Correct = $result.Correct; Rejected = $result.Rejected; Incorrect = $result.Incorrect }
  }
  @{ PublishedSource = $published; CheckedCyrillicPath = $work; CoreSha256 = $coreHash; Passed = $true; Modes = $summary } |
    ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $reports 'summary.json') -Encoding utf8
  Write-Host "Published OCR smoke passed; reports: $reports"
}
finally {
  Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
