$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('AIUsageMonitor-installer-test-' + [Guid]::NewGuid().ToString('N'))
$stage = Join-Path $root ('.ai-usage-update-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
try {
    $installer = Join-Path $stage 'install.ps1'
    dotnet run --project (Join-Path $PSScriptRoot 'Updates.csproj') -c Release -- --write-installer $installer
    if ($LASTEXITCODE -ne 0) { throw 'Cannot export installer script' }
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $source = Join-Path $root 'Stub.cs'
    $target = Join-Path $root 'AI Usage Monitor.exe'
    $payload = Join-Path $stage 'update.exe'
    foreach ($version in @('old', 'new')) {
        $code = @'
using System;
using System.IO;
using System.Threading;
class Stub {
    static void Main(string[] args) {
        if (args.Length > 0) { Thread.Sleep(2500); return; }
        File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "started.txt"), "VERSION");
    }
}
'@
        [IO.File]::WriteAllText($source, $code.Replace('VERSION', $version))
        $output = $payload
        if ($version -eq 'old') { $output = $target }
        & $csc /nologo /target:winexe "/out:$output" $source
        if ($LASTEXITCODE -ne 0) { throw 'Cannot compile disposable stub' }
    }
    $hash = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash
    $parentProcess = Start-Process -FilePath $target -ArgumentList 'wait' -PassThru -WindowStyle Hidden
    $powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    # Script runs synchronously; it waits for the disposable old process to finish naturally.
    & $powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $installer -ParentId $parentProcess.Id -Target $target -ExpectedHash $hash
    if ($LASTEXITCODE -ne 0) { throw 'Installer failed' }
    $marker = Join-Path $root 'started.txt'
    for ($attempt = 0; $attempt -lt 50 -and -not (Test-Path -LiteralPath $marker); $attempt++) { Start-Sleep -Milliseconds 100 }
    if (-not (Test-Path -LiteralPath $marker)) { throw 'Updated stub did not restart' }
    if ([IO.File]::ReadAllText($marker) -ne 'new') { throw 'Old stub restarted instead of update' }
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $hash) { throw 'Installed hash differs' }
    if (Test-Path -LiteralPath $stage) { throw 'Successful update staging was not cleaned up' }
    Write-Output 'PASS disposable EXE: wait for natural exit, replace, restart new version, clean up'
}
finally {
    # Only delete the exact freshly-created temporary fixture directory.
    $resolved = [IO.Path]::GetFullPath($root)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $resolved.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture escaped temporary directory' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
