$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $projectRoot "src\AppAccelerationBasket.cs"
$outputDirectory = Join-Path $projectRoot "dist"
$output = Join-Path $outputDirectory "应用加速箩筐.exe"
$checksum = Join-Path $outputDirectory "SHA256SUMS.txt"

$compilerCandidates = @(
    "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)
$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compiler) {
    throw "没有找到 .NET Framework C# 编译器。"
}

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

& $compiler /nologo /target:winexe /warn:4 /optimize+ /out:$output `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    /reference:System.Web.Extensions.dll `
    /reference:Microsoft.CSharp.dll `
    $source

if ($LASTEXITCODE -ne 0) {
    throw "编译失败，退出代码：$LASTEXITCODE"
}

$hash = Get-FileHash -LiteralPath $output -Algorithm SHA256
("{0}  {1}" -f $hash.Hash, (Split-Path -Leaf $output)) | Set-Content -LiteralPath $checksum -Encoding ASCII

Write-Host "构建完成：$output"
Write-Host "SHA256：$($hash.Hash)"
