<#
  Сборка программы «Распознавание текста документов» на ПК с Windows.

  Что нужно один раз: .NET SDK 10 (https://dotnet.microsoft.com/download/dotnet/10.0, «SDK x64»)
  и доступ к nuget.org (оттуда скачиваются библиотеки при первой сборке).

  Запуск из папки репозитория:
      powershell -ExecutionPolicy Bypass -File scripts\build.ps1
  С тестами (дольше):
      powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Test

  Результат: dist\Распознавание текста документов-Setup.exe. Его кладут в папку обновлений на
  сетевом диске; запуск с двойного щелчка ставит программу, программа сама находит там новые версии.
#>
param([switch]$Test)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$title = 'Распознавание текста документов'
$work = Join-Path $root 'artifacts\setup'
$app = Join-Path $work 'app'
$setupOut = Join-Path $work 'setup'
$zip = Join-Path $work 'app.zip'
$dist = Join-Path $root 'dist'
$target = Join-Path $dist "$title-Setup.exe"

function Step($text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }

Step 'Проверка .NET SDK'
$sdk = & dotnet --list-sdks 2>$null | Where-Object { $_ -match '^10\.' }
if (-not $sdk) { throw '.NET SDK 10 не найден. Установите его с https://dotnet.microsoft.com/download/dotnet/10.0 (SDK x64) и запустите сборку снова.' }

if ($Test) {
  Step 'Тесты'
  dotnet test tests\Core.Tests -c Release
  if ($LASTEXITCODE -ne 0) { throw 'Тесты не прошли: сборка остановлена.' }
}

Step 'Публикация программы (папка)'
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish src\App\App.csproj -c Release -r win-x64 --self-contained true -o $app
if ($LASTEXITCODE -ne 0) { throw 'Программа не собралась.' }

Step 'Библиотеки Microsoft Visual C++ рядом с программой (права администратора не нужны)'
$vc = 'vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll', 'msvcp140_1.dll', 'msvcp140_2.dll', 'vcomp140.dll', 'concrt140.dll'
$sys = Join-Path $env:WINDIR 'System32'
foreach ($dll in $vc) {
  $from = Join-Path $sys $dll
  if (Test-Path $from) { Copy-Item $from $app -Force; Write-Host "  $dll" }
  else { Write-Warning "$dll не найден в $sys. Установите «Microsoft Visual C++ 2015-2022 Redistributable x64» на этом ПК и соберите снова." }
}

Step 'Проверка компонентов распознавания'
$check = Start-Process -FilePath (Join-Path $app 'DocumentTextRecognition.exe') -ArgumentList '--check' -Wait -PassThru -NoNewWindow
if ($check.ExitCode -ne 0) { throw 'Компоненты распознавания не запускаются (Tesseract или PaddleOCR). Подробности: запустите DocumentTextRecognition.exe --check из папки artifacts\setup\app.' }
Write-Host '  Tesseract и PaddleOCR запускаются.'

Step 'Архив программы'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($app, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

Step 'Установщик'
dotnet publish src\Setup\Setup.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $setupOut
if ($LASTEXITCODE -ne 0) { throw 'Установщик не собрался.' }

New-Item -ItemType Directory -Force -Path $dist | Out-Null
Copy-Item (Join-Path $setupOut 'DocumentTextRecognition.Setup.exe') $target -Force
# Архив дописывается в конец установщика: смещение (8 байт) и метка DTRPAYLD.
$stream = [System.IO.File]::Open($target, [System.IO.FileMode]::Append)
try {
  $offset = $stream.Position
  $bytes = [System.IO.File]::OpenRead($zip)
  try { $bytes.CopyTo($stream) } finally { $bytes.Dispose() }
  $tail = [BitConverter]::GetBytes([long]$offset) + [System.Text.Encoding]::ASCII.GetBytes('DTRPAYLD')
  $stream.Write($tail, 0, $tail.Length)
} finally { $stream.Dispose() }

$size = [math]::Round((Get-Item $target).Length / 1MB)
Step "Готово: $target ($size МБ)"
Write-Host 'Положите файл в папку обновлений на сетевом диске. Установка: двойной щелчок по файлу.'
