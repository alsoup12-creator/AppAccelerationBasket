$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$outputDirectory = Join-Path $projectRoot 'dist\test-results'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sessionTest = Join-Path $outputDirectory 'ProxySessionTests.exe'
& $compiler /nologo /target:exe /platform:x64 /warn:4 /out:$sessionTest /reference:System.Web.Extensions.dll `
    (Join-Path $PSScriptRoot 'ProxySessionTests.cs') (Join-Path $projectRoot 'src\SystemProxySession.cs')
if ($LASTEXITCODE -ne 0) { throw 'Session test compilation failed.' }
& $sessionTest
if ($LASTEXITCODE -ne 0) { throw 'Session tests failed.' }
$uiTest = Join-Path $outputDirectory 'BasketUiTests.exe'
& $compiler /nologo /target:exe /platform:x64 /warn:4 /main:BasketUiTests /out:$uiTest `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Management.dll `
    /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:Microsoft.CSharp.dll `
    (Join-Path $PSScriptRoot 'BasketUiTests.cs') (Join-Path $projectRoot 'src\AppAccelerationBasket.cs') `
    (Join-Path $projectRoot 'src\PackagedProxy.cs') (Join-Path $projectRoot 'src\SystemProxySession.cs')
if ($LASTEXITCODE -ne 0) { throw 'UI test compilation failed.' }
& $uiTest
if ($LASTEXITCODE -ne 0) { throw 'UI tests failed.' }
