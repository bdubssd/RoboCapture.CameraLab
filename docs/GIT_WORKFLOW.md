# Git workflow

The repository includes a `post-commit` hook that pushes significant commits automatically.

Enable it once on each computer:

    git config core.hooksPath .githooks

The default significance threshold is 5 or more changed files, or 100 or more inserted/deleted lines. Smaller commits stay local until you run:

    git push

To customize the thresholds for the current terminal session:

    $env:ROBOCAPTURE_PUSH_FILES = "3"
    $env:ROBOCAPTURE_PUSH_LINES = "50"

The hook does not bypass GitHub authentication. Complete GitHub sign-in once on each computer when Git requests it.

## Studio bootstrap download

Download [setup-studio.ps1](../setup-studio.ps1) on a new computer, then run it from PowerShell:

    powershell -ExecutionPolicy Bypass -File .\setup-studio.ps1

It clones or updates the project, enables the hook, and runs the test suite when the .NET SDK is installed.