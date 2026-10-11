# MXC GitHub Actions Runner

[![Build and test](https://github.com/MogamiTsuchikawa/mxc-windows-gh-action/actions/workflows/ci.yml/badge.svg)](https://github.com/MogamiTsuchikawa/mxc-windows-gh-action/actions/workflows/ci.yml)

English | [日本語](README.ja.md)

A C# / .NET 10 CLI that runs a GitHub Actions self-hosted runner inside a Windows [MXC](https://github.com/microsoft/mxc) ProcessContainer. Each invocation registers an ephemeral runner, accepts **one job**, and then exits. Its local work directory is removed by default.

Use it to run Windows builds with tools installed on your own machine, while giving each job an explicit filesystem boundary and a fresh profile. It handles runner downloads, registration, process lifetime, logs, and cleanup so these steps do not need a separate script.

## Get Started

**[Follow the English setup guide →](GET_STARTED.md)** · **[日本語の導入手順 →](GET_STARTED.ja.md)**

There is no GitHub Release package yet. Download the `mxc-gh-runner-windows` artifact from a successful [Build and test workflow](https://github.com/MogamiTsuchikawa/mxc-windows-gh-action/actions/workflows/ci.yml) run while signed in to GitHub, or build from source. The guide covers both routes, configuration, authentication, and your first job.

## How it works

1. The host downloads the official GitHub Actions runner and verifies its SHA-256 checksum.
2. The CLI creates a fresh run directory and starts a worker inside MXC.
3. The worker registers with `--ephemeral --disableupdate` and waits for a matching job.
4. GitHub assigns one job; its steps and post-job actions run inside MXC.
5. The runner exits. The CLI keeps its forwarded output log and deletes the run directory by default.

Start `run` again to accept another job. The CLI process exit code describes the runner process; **check GitHub Actions for the workflow result**.

## Features

- `doctor` checks MXC support; `smoke` verifies actual contained .NET startup, standard input, credential-environment isolation, and file I/O without GitHub credentials.
- `plan` validates configuration; `prepare` downloads and verifies the runner without registering it.
- Host access tokens stay on the host. Registration tokens reach the worker through standard input, not command-line arguments or configuration files.
- Each run gets its own profile, temporary files, and NuGet cache. Host environment variables are not inherited wholesale; SDK/MSBuild boundary files stop searches into parent host projects.
- Filesystem permissions allow writes to the run directory and reads of Windows, .NET, the CLI, and configured tools. State directories must remain separate from these read-only trees.
- CI artifacts include the verified official MinGit BusyBox distribution for Git helpers used by `actions/checkout`, plus its original licenses and notices.

## Requirements

| Requirement | Details |
| --- | --- |
| Windows | A Windows host with a usable MXC ProcessContainer backend. Confirm with `doctor` and `smoke`; the recorded end-to-end validation used Windows build 26300, x64. |
| .NET | .NET 10 runtime to run the framework-dependent CLI. A .NET 10 SDK is required to build the CLI or run C# build examples; the source repository selects SDK 10.0.200 or a later 10.0 feature band. |
| GitHub | A repository on `github.com` and permission to register its self-hosted runners. |
| Workflow tools | Install the tools your job needs and add their directories to `toolPaths`. Use MinGit BusyBox for checkout; install PowerShell 7 for workflows using `shell: pwsh`. Windows PowerShell is available with `shell: powershell`. |
| Network | Outbound access to GitHub, runner downloads, and any package sources needed by your workflow. |

The CLI accepts `x64` and `arm64` configurations matching the host. The distributed Git bundle and recorded real-job validation are **x64**; ARM64 has not been validated on hardware.

## Current limits

- Repository runners on `github.com` only. GitHub Enterprise Server, organization runners, a persistent service, autoscaling, and Linux container jobs are outside the current scope.
- MSYS/Cygwin shells, including standard Git for Windows shell helpers, cannot initialize under the current MXC policy. Use MinGit **BusyBox** for Git helpers and PowerShell for workflow steps; this does not provide Bash support.
- Outbound network traffic is allowed; inbound traffic and host loopback are denied. There is no destination-domain allowlist.
- The MXC UI-disable switch remains off because CoreCLR needs Windows UI subsystem initialization. Clipboard access and input injection are denied; this is not a configuration that disables every GUI API.
- A registration-token-only run interrupted before completing a job can leave an offline runner registration. Remove it in the repository's Actions settings if reported. Access-token mode also attempts API cleanup.
- Work directories are deleted by default. Upload job artifacts from the workflow before it finishes; retained work directories can contain job data and runner credentials.

## Documentation and validation

- [Get Started](GET_STARTED.md): installation, authentication, configuration, command reference, and troubleshooting.
- [Basic workflow](examples/mxc-workflow.yml) and [checkout + C# validation workflow](examples/e2e-validation.yml).
- [Configuration example](examples/mxc-runner.example.json).
- [Architecture](docs/architecture.md), [Git compatibility](docs/git-compatibility.md), and [verification record](docs/verification.md) (Japanese).

The initial implementation passed **108 automated tests**, also confirmed by the public Windows CI. A real GitHub Actions job passed private-repository checkout, isolated profile and credential checks, Git, C# build/run, post-job cleanup, and runner removal. Hosted CI tests are separate from real MXC host validation; see the verification record for both environments.

## License

Original project source and documentation are licensed under the [MIT License](LICENSE), Copyright (c) 2026 MogamiTsuchikawa.

Third-party software retains its original terms. The project license does not replace Git/MinGit BusyBox's GPLv2, Microsoft.Mxc.Sdk's Microsoft copyright and MIT notice, or .NET distribution terms and notices. See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
