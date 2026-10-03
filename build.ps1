$ErrorActionPreference = 'Stop'
foreach ($name in 'Extract', 'Import') {
    & dotnet publish (Join-Path $PSScriptRoot "src\$name\NTAA-$name.csproj") -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $PSScriptRoot 'dist')
    if ($LASTEXITCODE -ne 0) { throw "NTAA-$name build failed." }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $PSScriptRoot 'dist\README.md') -Force
