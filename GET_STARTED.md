# Get Started

English | [日本語](GET_STARTED.ja.md) · [Project overview](README.md)

This guide takes a Windows host from installation to one GitHub Actions job running inside MXC. Commands use Windows PowerShell and assume Windows x64. Install the .NET 10 runtime for the CLI; the first C# job below and source builds require the .NET 10 SDK. Your host must support MXC ProcessContainer; `doctor` and `smoke` check this below. PowerShell 7 is optional unless a workflow uses `shell: pwsh`.

## 1. Get the CLI

Choose an existing CI artifact or build from source. There is currently no GitHub Release package.

### Option A: Download a CI artifact

1. Sign in to GitHub and open [Build and test](https://github.com/MogamiTsuchikawa/mxc-windows-gh-action/actions/workflows/ci.yml).
2. Open a successful run for the `main` branch and download **mxc-gh-runner-windows** from its **Artifacts** section.
3. Extract the **entire archive** to a tools directory outside your user profile, for example `D:\tools\mxc-gh-runner`. Keep the executable, DLLs, native SDK assets, `tools/`, and license files together.
4. Open PowerShell in the directory containing `mxc-gh-runner.exe`:

```powershell
Set-Location D:\tools\mxc-gh-runner
.\mxc-gh-runner.exe --help
```

The artifact is framework-dependent: it does not include the .NET runtime. It includes the official MinGit BusyBox distribution, which `init` selects automatically. Tool executables under user-profile directories can be denied by inherited Windows ACLs even when MXC grants read access; a normal tools directory outside the profile is the tested arrangement.

Continue with step 2. You do not need the manual Git download below.

### Option B: Build from source

Use a checkout outside your user profile, install Git on the host for cloning, and install a .NET 10 SDK satisfying the repository's `global.json` (10.0.200 with `latestFeature` roll-forward). Run these commands from your chosen source parent directory:

```powershell
git clone https://github.com/MogamiTsuchikawa/mxc-windows-gh-action.git
Set-Location mxc-windows-gh-action
dotnet restore Mxc.GitHubRunner.slnx --locked-mode
dotnet test Mxc.GitHubRunner.slnx -c Release --no-restore
dotnet publish src/Mxc.GitHubRunner -c Release --no-restore -o artifacts/publish
Set-Location artifacts/publish
.\mxc-gh-runner.exe --help
```

Check that each command succeeds before continuing. `dotnet publish` produces the CLI but does **not** download MinGit BusyBox. Keep the whole publish directory when copying the app elsewhere.

For `actions/checkout`, install the official **BusyBox** variant of MinGit. Ordinary Git for Windows can run commands such as `git --version` while its MSYS shell helpers still fail inside MXC. This setup supplies native Git helpers, not a Bash shell.

The following downloads the pinned x64 package from [Git for Windows v2.56.0.windows.2](https://github.com/git-for-windows/git/releases/tag/v2.56.0.windows.2), verifies its SHA-256, and extracts it without replacing an existing installation. Change `D:\tools\MinGitBusyBox` to an appropriate tools directory outside your profile and state directory:

```powershell
$gitRoot = 'D:\tools\MinGitBusyBox'
if (Test-Path -LiteralPath $gitRoot) {
    throw 'The MinGit BusyBox destination already exists.'
}
$gitArchive = Join-Path $env:TEMP ('mxc-mingit-' + [guid]::NewGuid().ToString('N') + '.zip')
Invoke-WebRequest 'https://github.com/git-for-windows/git/releases/download/v2.56.0.windows.2/MinGit-2.56.0.2-busybox-64-bit.zip' -OutFile $gitArchive
$expected = 'a8a9172a6747926648b78a3411f3b94813dd22da8a8ec5dbe61122bc414edd63'
if ((Get-FileHash -LiteralPath $gitArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) {
    throw 'MinGit BusyBox SHA-256 mismatch.'
}
Expand-Archive -LiteralPath $gitArchive -DestinationPath $gitRoot
Remove-Item -LiteralPath $gitArchive
```

Keep the distribution's license and notice files. Step 2 shows how to select this installation. More background is available in [Git compatibility](docs/git-compatibility.md) (Japanese).

## 2. Create and edit the configuration

From the directory containing the executable:

```powershell
.\mxc-gh-runner.exe init
notepad .\mxc-runner.json
```

Change `repository` to your target repository URL, such as `https://github.com/your-account/your-repository`. Use an HTTPS `github.com` URL without credentials, port, query, or fragment. Replace the generated `OWNER/REPO` placeholder before `run`.

Review `toolPaths`. `init` detects bundled MinGit BusyBox first, otherwise the standard Git installation, and detects PowerShell 7 in its usual installation directory. It does not download tools. For a manual Git installation, replace the Git entry with its `cmd` directory. This is a field to edit in the generated JSON, not the whole file:

```json
"toolPaths": [
  "D:\\tools\\MinGitBusyBox\\cmd"
]
```

Add PowerShell 7's directory (usually `C:\Program Files\PowerShell\7`) if it is installed and your jobs need `shell: pwsh`. Use `shell: powershell` for the built-in Windows PowerShell. Entries must be existing directories; their order determines PATH lookup. Put MinGit BusyBox before any other Git. Add other required build tools here because jobs do not inherit the host's full PATH or user profile. Windows and the CLI's .NET installation are added automatically.

Keep the `stateDirectory` chosen by `init` unless you need another location. The usual default is `.mxc-runner` beside the configuration file. When this would overlap the CLI's read-only installation tree, `init` chooses `%LOCALAPPDATA%\MxcGitHubRunner`. State must neither contain nor sit inside the CLI, Windows, .NET, or configured tool trees. This separation is checked before downloading or registering a runner.

`init` never overwrites an existing file. To keep configuration elsewhere, use `init --config D:\mxc-config\runner.json`, then pass the same `--config` option to subsequent commands except `doctor`. Relative paths in JSON are resolved from the configuration file's directory.

## 3. Check the host and prepare the runner

```powershell
.\mxc-gh-runner.exe doctor
.\mxc-gh-runner.exe plan
.\mxc-gh-runner.exe smoke
.\mxc-gh-runner.exe prepare
```

Check each command's output before continuing:

- `doctor` reports whether MXC ProcessContainer is available on this Windows host.
- `plan` validates the configuration and prints resolved paths.
- `smoke` actually starts .NET inside MXC and checks standard input, credential-environment isolation, and temporary-file I/O. It needs no GitHub token.
- `prepare` downloads the official Actions runner, verifies the release SHA-256, and caches it. It does not register a runner and needs no repository credential.

## 4. Add a workflow to the target repository

Create `.github/workflows/mxc-check.yml` in the repository named by `repository`, commit it, and make it available on the repository's default branch for manual dispatch. This example needs MinGit BusyBox and the .NET 10 SDK on the host:

```yaml
name: MXC first job

on:
  workflow_dispatch:

permissions:
  contents: read

jobs:
  check:
    runs-on: [self-hosted, Windows, X64, mxc]
    timeout-minutes: 10
    steps:
      - uses: actions/checkout@v7.0.1
      - name: Build and run C#
        shell: powershell
        run: |
          $ErrorActionPreference = 'Stop'
          dotnet --version
          if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
          $project = Join-Path $env:RUNNER_TEMP 'mxc-first-job'
          dotnet new console --framework net10.0 --output $project
          if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
          dotnet run --project $project --no-restore
          if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
```

The runner adds the usual `self-hosted`, `Windows`, and architecture labels; `mxc` comes from the default configuration. Match any custom labels you change. For an ARM64 host use `ARM64` instead of `X64` and matching tool packages; real-hardware validation currently covers x64 only.

The [basic example](examples/mxc-workflow.yml) also builds C# and requires the .NET SDK. The [end-to-end example](examples/e2e-validation.yml) adds `actions/checkout@v7.0.1`, Git checks, and C# build/run; it requires MinGit BusyBox, the .NET SDK, and PowerShell 7. Copy either example into your target repository's `.github/workflows/` to use it. Upload any artifacts within the job because local work is deleted afterward.

## 5. Authenticate and run one job

In the target repository, open **Settings → Actions → Runners → New self-hosted runner** and obtain a fresh registration token. You only need the token; the CLI handles the runner's configuration and startup.

Enter it in the host PowerShell session without echoing it, then start the CLI:

```powershell
$registrationSecret = Read-Host 'Runner registration token' -AsSecureString
try {
    $env:MXC_RUNNER_REGISTRATION_TOKEN = [System.Net.NetworkCredential]::new('', $registrationSecret).Password
    .\mxc-gh-runner.exe run
} finally {
    Remove-Item Env:\MXC_RUNNER_REGISTRATION_TOKEN -ErrorAction SilentlyContinue
    $registrationSecret.Dispose()
}
```

Keep that terminal open. When the runner is listening, open the target repository's **Actions → MXC first job → Run workflow**. The CLI does not dispatch workflows itself. A matching queued job is accepted; after that one job and its post-job steps finish, the runner exits. Start `run` again with a fresh token for the next job.

Never put tokens in JSON, workflow files, command arguments, or committed scripts. The registration token is passed to the contained worker through standard input. The two host bootstrap credential variables are not included in the job environment.

### Alternative: Access-token mode

You can instead set `MXC_GITHUB_TOKEN` to a personal access token or other GitHub access token permitted to manage the target repository's self-hosted runners. A repository's workflow `GITHUB_TOKEN` is not the bootstrap credential described here.

```powershell
$accessSecret = Read-Host 'GitHub runner-management token' -AsSecureString
try {
    $env:MXC_GITHUB_TOKEN = [System.Net.NetworkCredential]::new('', $accessSecret).Password
    .\mxc-gh-runner.exe run
} finally {
    Remove-Item Env:\MXC_GITHUB_TOKEN -ErrorAction SilentlyContinue
    $accessSecret.Dispose()
}
```

In this mode, the host calls GitHub's API to create a registration token and attempts to remove any remaining runner registration at shutdown. The access token is not sent into MXC. If both environment variables are set, registration uses `MXC_RUNNER_REGISTRATION_TOKEN`, while `MXC_GITHUB_TOKEN` is used for remote cleanup.

Access-token API behavior is covered by automated tests with simulated HTTP responses. The recorded real GitHub job used registration-token authentication.

With only a registration token, interruption or timeout before a job completes can leave an offline registration. If the CLI reports this, remove the displayed runner name under **Settings → Actions → Runners**.

## Results, logs, and cleanup

Check the workflow's conclusion in GitHub Actions. A CLI exit code of `0` means the runner process completed successfully; it does **not** establish that the workflow passed.

The resolved `stateDirectory` contains:

| Directory | Purpose |
| --- | --- |
| `packages/` | Verified runner package cache, retained for later runs. |
| `runs/<GUID>/` | Per-run runner installation, workspace, profile, and temporary data; deleted by default. |
| `logs/` | Retained forwarded standard-output and standard-error logs, named for the generated runner. |

`keepWorkDirectory: true` keeps the run directory for investigation; it can contain job data and runner credentials. It does not keep the runner process alive. Detailed job output and the workflow conclusion remain in GitHub Actions.

## Command reference

| Command | Purpose |
| --- | --- |
| `init [--config PATH]` | Create a new configuration without overwriting a file. |
| `doctor` | Check Windows/MXC availability; accepts no configuration option. |
| `plan [--config PATH]` | Validate and display resolved configuration. |
| `smoke [--config PATH]` | Run the credential-free local MXC check. |
| `prepare [--config PATH]` | Download and verify the official runner. |
| `run [--config PATH]` | Register, wait for and execute one job, then clean up. |

The default configuration is `mxc-runner.json` in the current directory. Exit codes are `0` for command success, `1` for errors, `124` for the contained runner's lifetime timeout, and `130` for Ctrl+C. Otherwise, `run` returns the runner process's exit code.

## Configuration reference

See [the JSON example](examples/mxc-runner.example.json). Unknown and duplicate properties are rejected, including names differing only by case. There are no token fields.

| Property | Default and constraints |
| --- | --- |
| `repository` | `https://github.com/OWNER/REPO`; replace the placeholder before running. |
| `runnerVersion` | `latest`, or a three-part version such as `2.338.0`. |
| `architecture` | `init` selects the host architecture; `x64` or `arm64`, matching the host at execution. |
| `stateDirectory` | `.mxc-runner`, or a safe external location selected by `init`. Must be separate from read-only application/tool trees and cannot be a filesystem root. |
| `namePrefix` | `mxc`; 1–40 ASCII letters, digits, `.`, `_`, or `-`, starting with a letter or digit. A unique suffix is added per run. |
| `labels` | `["mxc"]`; at most 20 labels, each 1–64 characters using the same character rules as `namePrefix`. |
| `toolPaths` | Existing directories used for PATH and read access; `init` populates detected Git and PowerShell 7 locations. Git's `cmd` entry also grants read access to its installation tree. |
| `timeoutMinutes` | `120`; 1–1440 minutes for the contained registration, job waiting, and execution lifetime. Download/preparation is outside this limit. |
| `keepWorkDirectory` | `false`; retain run data after exit when set to `true`. |

## Troubleshooting

| Symptom | What to check |
| --- | --- |
| The executable asks for .NET | Install the .NET 10 runtime matching the CLI architecture. Install the SDK if the job needs `dotnet new`, restore, or build. |
| `doctor` reports unsupported MXC | Check the reported backend reason and Windows version. A successful build alone does not establish host support; pass `doctor` and `smoke` before registering a runner. |
| `smoke` or a tool fails with access denied | Keep the full CLI/tool installation outside the user profile, and state outside all read-only tool trees. Check host file permissions and the reported path. |
| A configured directory does not exist | Remove unused entries or correct `toolPaths`; do not leave the PowerShell 7 example path when it is not installed. |
| Git helpers report MSYS or named-object errors | Select MinGit **BusyBox**, first in `toolPaths`. A passing `git --version` does not verify shell helpers. `shell: bash` remains unsupported. |
| `pwsh` cannot be found | Install PowerShell 7 and add its directory to `toolPaths`, or use `shell: powershell` if the script supports Windows PowerShell. |
| The job remains queued | Confirm the repository URL, runner online state, all requested labels, and architecture. Each invocation accepts only one job. |
| Registration fails | Obtain a fresh repository registration token, or check that the access token can manage that repository's runners. Tokens belong in the host environment. |
| A run times out while waiting | Dispatch a matching job or increase `timeoutMinutes`. Check for the offline registration warning after interruption. |
| `dotnet --info` errors inside MXC | Some host service queries are denied. Use `dotnet --version` when checking the SDK, and verify the actual build command. |
| The CLI exits `0` but the job failed | Inspect the GitHub Actions job logs; runner-process status and workflow conclusion are different results. |

For containment behavior and other current limits, see the [overview](README.md). The [verification record](docs/verification.md) and [architecture notes](docs/architecture.md) are currently in Japanese.
