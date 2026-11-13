$ErrorActionPreference = 'Stop'
function FileHash([string]$file) {
    $sha = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($file)
    try { return [Convert]::ToBase64String($sha.ComputeHash($stream)) } finally { $stream.Dispose(); $sha.Dispose() }
}
$root = $PSScriptRoot
$target = Join-Path $root 'AI Usage Monitor.exe'
$legacy = Join-Path $root 'AI-Usage-Monitor-win-x64.exe'
$project = Join-Path $root 'src\UsageMonitor.csproj'
if (!(Test-Path -LiteralPath $project)) { $project = Join-Path $root '소스\UsageMonitor.csproj' }
if (!(Test-Path -LiteralPath $project)) { throw '빌드할 소스를 찾지 못했습니다.' }
$backups = @(Get-ChildItem -LiteralPath $root -File | Where-Object { $_.Name -match '^AI( Usage Monitor|-Usage-Monitor-win-x64)\.exe\.previous-[a-fA-F0-9]{32}$' })
$paths = @($target, $legacy) + @($backups | ForEach-Object FullName)
foreach ($app in Get-Process | Where-Object { $_.Path -and $paths -contains $_.Path }) {
    if (!$app.CloseMainWindow() -or !$app.WaitForExit(3000)) { $app.Kill(); $app.WaitForExit() }
}
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$stage = Join-Path $tempRoot ('AIUsageBuild-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    & dotnet publish $project -c Release -o $stage -p:RuntimeFrameworkVersion=9.0.11 -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw '빌드 실패: 기존 EXE를 유지합니다.' }
    $exe = Join-Path $stage 'AI Usage Monitor.exe'
    $zip = Join-Path $stage 'AI Usage Monitor.zip'
    Compress-Archive -LiteralPath $exe -DestinationPath $zip
    Copy-Item -LiteralPath $exe -Destination $target -Force
    Copy-Item -LiteralPath $zip -Destination $root -Force
    if ((FileHash $exe) -ne (FileHash $target)) { throw 'EXE 복사 검증 실패' }
    if (Test-Path -LiteralPath $legacy) { Remove-Item -LiteralPath $legacy }
    foreach ($backup in $backups) { Remove-Item -LiteralPath $backup.FullName }
    Write-Host "실행 파일: $target"
    Write-Host "공유 파일: $(Join-Path $root 'AI Usage Monitor.zip')"
} finally {
    $resolved = (Resolve-Path -LiteralPath $stage).Path
    if ([IO.Path]::GetDirectoryName($resolved).TrimEnd('\') -eq $tempRoot.TrimEnd('\') -and [IO.Path]::GetFileName($resolved) -match '^AIUsageBuild-[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
