[CmdletBinding()]
param(
    [string]$Destination = (Join-Path (Get-Location) 'RoboCapture.CameraLab')
)

$ErrorActionPreference = 'Stop'
$repositoryUrl = 'https://github.com/bdubssd/RoboCapture.CameraLab.git'

function Find-Git {
    $command = Get-Command git -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $standardPath = 'C:\Program Files\Git\cmd\git.exe'
    if (Test-Path $standardPath) { return $standardPath }

    throw 'Git is required. Install Git for Windows from https://git-scm.com/download/win, then run this script again.'
}

$git = Find-Git

if (Test-Path (Join-Path $Destination '.git')) {
    Write-Host "Updating $Destination"
    & $git -C $Destination pull --ff-only
}
elseif (Test-Path $Destination) {
    throw "Destination exists but is not a Git repository: $Destination"
}
else {
    Write-Host "Cloning project to $Destination"
    & $git clone $repositoryUrl $Destination
}

& $git -C $Destination config core.hooksPath .githooks
Write-Host 'Automatic significant-change pushes are enabled.'

if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    Write-Host 'Running tests...'
    & dotnet test (Join-Path $Destination 'RoboCapture.CameraLab.sln')
}
else {
    Write-Warning 'The .NET 8 SDK was not found. Install it before running the application.'
}

Write-Host "Ready: $Destination"