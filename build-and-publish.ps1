if (Test-Path -Path .\BravoLights\bin\Release) {
	Remove-Item -Path .\BravoLights\bin\Release -Recurse
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vsPath = & $vswhere -latest -products '*' -version '[17.0,18.0)' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($vsPath)) {
	throw 'Visual Studio 2022 with the C++ build tools was not found.'
}

$msbuild = Join-Path $vsPath 'MSBuild\Current\Bin\MSBuild.exe'
if (-not (Test-Path $msbuild)) {
	throw "Visual Studio MSBuild was not found at $msbuild"
}
$env:VCTargetsPath = Join-Path $vsPath 'MSBuild\Microsoft\VC\v170\'

$sdkRoot = $env:MSFS2024_SDK
if (-not (Test-Path (Join-Path $sdkRoot 'WASM\vs\2022\Microsoft.Cpp.MSFS.Common.props'))) {
	$sdkRoot = 'C:\MSFS 2024 SDK\'
}
if (-not (Test-Path (Join-Path $sdkRoot 'WASM\vs\2022\Microsoft.Cpp.MSFS.Common.props'))) {
	throw 'The MSFS 2024 SDK WASM toolset could not be found.'
}
$sdkRoot = (Resolve-Path $sdkRoot).Path.TrimEnd('\') + '\'
$env:MSFS2024_SDK = $sdkRoot
$env:MSFS_SDK = $sdkRoot

& $msbuild .\BravoLights.sln /restore /m /p:Configuration=Release '/p:Platform=Any CPU' /v:minimal
if ($LASTEXITCODE -ne 0) {
	throw "Visual Studio solution build failed with exit code $LASTEXITCODE"
}

dotnet publish .\BravoLights\BravoLights.csproj -p:PublishProfile=FolderProfile -p:Configuration=Release
if ($LASTEXITCODE -ne 0) {
	throw "dotnet publish failed with exit code $LASTEXITCODE"
}
$path = Resolve-Path "BravoLights\bin\Release\net5.0-windows\publish\BetterBravoLights.exe"

if (Test-Path -Path BetterBravoLights) {
	Remove-Item -Path BetterBravoLights -Recurse
}

mkdir BetterBravoLights
mkdir BetterBravoLights\Program
Copy-Item -Path "BravoLights\bin\Release\net5.0-windows\publish\*" -Destination "BetterBravoLights\Program" -Recurse
Copy-Item -Path "BravoLights\install.bat" -Destination "BetterBravoLights"
Copy-Item -Path "BravoLights\uninstall.bat" -Destination "BetterBravoLights"
Copy-Item -Path "BravoLights\LICENSES.md" -Destination "BetterBravoLights"
Move-Item -Path "BetterBravoLights\Program\Config.User.ini" -Destination "BetterBravoLights\Config.ini"

$o = [system.diagnostics.fileversioninfo]::GetVersionInfo($path)
Write-Output "Better Bravo Lights $($o.ProductVersion)" | Out-File -Encoding UTF8 BetterBravoLights\VERSION.txt

$packageToolPath = Join-Path $sdkRoot 'Tools\bin\fspackagetool.exe'
$packageProject = Join-Path (Get-Location).Path 'MSFSWASMProject\BetterBravoLightsLVars.xml'
$packageOutput = Join-Path (Get-Location).Path 'BetterBravoLights\Program'
$packageArguments = @('"' + $packageProject + '"', '-outputdir', '"' + $packageOutput + '"', '-rebuild', '-nopause')
$packageTool = Start-Process -Wait -PassThru -FilePath $packageToolPath -WorkingDirectory (Split-Path $packageProject) -ArgumentList $packageArguments
if ($packageTool.ExitCode -ne 0) {
	throw "fspackagetool failed with exit code $($packageTool.ExitCode)"
}

$wasmModule = Join-Path (Get-Location).Path 'MSFSWASMProject\PackageSources\modules\WasmModule.wasm'
if (-not (Test-Path $wasmModule)) {
	throw "The built WASM module was not found at $wasmModule"
}
$includedPackage = Join-Path (Get-Location).Path 'BetterBravoLights\Program\Packages\better-bravo-lights-lvar-module'
if (-not (Test-Path (Join-Path $includedPackage 'manifest.json'))) {
	throw "fspackagetool did not create the included WASM package manifest at $includedPackage"
}
$moduleOutput = Join-Path $includedPackage 'modules'
New-Item -ItemType Directory -Force -Path $moduleOutput | Out-Null
Copy-Item -Path $wasmModule -Destination (Join-Path $moduleOutput 'WasmModule.wasm') -Force

Compress-Archive -Path "BetterBravoLights\*" -Force -DestinationPath BetterBravoLightsInstaller.zip
