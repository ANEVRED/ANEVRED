param([string]$OutputDirectory = '')
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot
[xml]$project=Get-Content -LiteralPath (Join-Path $projectRoot 'ANEVRED.csproj') -Raw
$version=[string]$project.Project.PropertyGroup.Version
if([string]::IsNullOrWhiteSpace($OutputDirectory)){$OutputDirectory=Join-Path $projectRoot ('bin/Releases/v'+$version)}
$publish=Join-Path $OutputDirectory ('ANEVRED-'+$version+'-win-x64')
dotnet publish (Join-Path $projectRoot 'ANEVRED.csproj') --configuration Release --runtime win-x64 --self-contained true --output $publish -p:DebugType=None -p:DebugSymbols=false --nologo
if($LASTEXITCODE -ne 0){throw 'Publish failed'}
# Copy the exact runtime-pack notices used by this build; do not guess license terms.
$assets=Get-Content -LiteralPath (Join-Path $projectRoot 'obj/project.assets.json') -Raw|ConvertFrom-Json
$packages=$assets.packageFolders.PSObject.Properties.Name
$notices=Join-Path $publish 'ThirdPartyNotices';New-Item -ItemType Directory -Path $notices -Force|Out-Null
foreach($name in @('Microsoft.NETCore.App.Runtime.win-x64','Microsoft.WindowsDesktop.App.Runtime.win-x64')){
 $library=$assets.libraries.PSObject.Properties|Where-Object{$_.Name.StartsWith($name+'/',[StringComparison]::OrdinalIgnoreCase)}|Select-Object -First 1
 if(-not $library){throw ('Runtime pack not resolved: '+$name)}
 $relative=$library.Value.path;$found=$false
 foreach($packagesRoot in $packages){$pack=Join-Path $packagesRoot $relative;if(Test-Path -LiteralPath $pack){
  $target=Join-Path $notices $name;New-Item -ItemType Directory -Path $target -Force|Out-Null
  $files=Get-ChildItem -LiteralPath $pack -File|Where-Object{$_.Name -match '^(LICENSE(\.TXT)?|THIRD-PARTY-NOTICES\.TXT)$'}
  if(-not $files){throw ('Runtime license missing: '+$name)}
  foreach($file in $files){Copy-Item -LiteralPath $file.FullName -Destination $target}
  $found=$true;break
 }}
 if(-not $found){throw ('Runtime pack directory missing: '+$name)}
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/GETTING_STARTED.md') -Destination (Join-Path $publish 'START_HERE.md')
if(Test-Path -LiteralPath (Join-Path $projectRoot 'LICENSE')){Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $publish}
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/RELEASE_0.3.5.md') -Destination (Join-Path $publish 'RELEASE_NOTES.md')
$zip=Join-Path $OutputDirectory ('ANEVRED-'+$version+'-win-x64.zip')
Compress-Archive -Path $publish -DestinationPath $zip -Force
$hash=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'SHA256SUMS.txt'),$hash+'  '+(Split-Path $zip -Leaf)+[Environment]::NewLine)
Write-Output $zip
