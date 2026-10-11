# Third-party notices

The [MIT license](LICENSE) at the root of this repository applies to this project's original source code and documentation. Third-party software retains its original copyright and license terms. This file does not relicense the SDK, Git distribution, .NET, or their dependencies.

The versions and license descriptions below were checked against the restored NuGet packages and the official MinGit distribution used for the 2026-10-11 validation. The lock files record the resolved NuGet versions. Original component license and notice files remain authoritative.

## Runtime dependency: Microsoft.Mxc.Sdk 1.0.0

- Source: [microsoft/mxc](https://github.com/microsoft/mxc).
- Package: [Microsoft.Mxc.Sdk 1.0.0](https://www.nuget.org/packages/Microsoft.Mxc.Sdk/1.0.0).
- The package's `microsoft.mxc.sdk.nuspec` declares a file license, `LICENSE.md`. That file states **MIT License**, with **Copyright (c) Microsoft Corporation**.
- The package includes the managed SDK and native runtime assets. The project's MIT copyright notice does not replace Microsoft's notice. The package license is reproduced below.

```text
MIT License

Copyright (c) Microsoft Corporation.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE
```

## Bundled Git for Windows / MinGit BusyBox

The Windows x64 bundle contains the unmodified contents of the official `MinGit-2.56.0.2-busybox-64-bit.zip` under `tools/MinGitBusyBox/`, including its license and notice files.

- Release and checksums: [Git for Windows v2.56.0.windows.2](https://github.com/git-for-windows/git/releases/tag/v2.56.0.windows.2).
- Original archive: [MinGit-2.56.0.2-busybox-64-bit.zip](https://github.com/git-for-windows/git/releases/download/v2.56.0.windows.2/MinGit-2.56.0.2-busybox-64-bit.zip).
- Archive SHA-256: `a8a9172a6747926648b78a3411f3b94813dd22da8a8ec5dbe61122bc414edd63`.
- Git source: [git-for-windows/git](https://github.com/git-for-windows/git).
- BusyBox source: [git-for-windows/busybox-w32](https://github.com/git-for-windows/busybox-w32).

| Component | Original license and verification material |
| --- | --- |
| Git for Windows 2.56.0.windows.2 | **GNU General Public License, version 2**, as reproduced in `tools/MinGitBusyBox/LICENSE.txt`; individual files may state additional terms. |
| BusyBox `v1.38.0.git-7099-gd8d8bb397` | **GPLv2**, explicitly identified by the shipped `ucrt64/bin/busybox.exe --help`; detailed copyright notices are in its upstream source distribution. |
| Git Credential Manager | **MIT**, Copyright GitHub, Inc. and contributors, in `ucrt64/doc/git-credential-manager/LICENSE`; dependency notices are in the adjacent `NOTICE`. Its source is [git-ecosystem/git-credential-manager](https://github.com/git-ecosystem/git-credential-manager). |
| curl | The **curl copyright and permission notice**, preserved in `ucrt64/share/licenses/curl/LICENSE`; this is not replaced by the project's MIT license. |
| OpenSSL | **Apache License 2.0**, preserved in `ucrt64/share/licenses/openssl/LICENSE`. |
| Other libraries and utilities in MinGit | Their original licenses and copyright notices are preserved in the directories listed below. The Git distribution is an aggregate of components; this project's MIT license does not apply to the aggregate. |

Preserved notice locations, relative to `tools/MinGitBusyBox/`:

- `LICENSE.txt`
- `ucrt64/share/licenses/`
- `usr/share/licenses/`
- `ucrt64/doc/git-credential-manager/LICENSE`
- `ucrt64/doc/git-credential-manager/NOTICE`

The Git Credential Manager `NOTICE` also reproduces notices for material from `GitHub/VisualStudio` and `dotnet/runtime`. These remain part of the original Git distribution. Do not remove or replace the original notice files when repackaging it.

## .NET SDK, runtime, and generated application host

The project targets .NET 10 and publishes a **framework-dependent** application. The full .NET SDK and shared runtime are installed separately; the publish output includes the SDK-generated application host. They are Microsoft/.NET components, not original source created by this project.

The validation machine used SDK 10.0.204 and runtime 10.0.8. Its .NET installation provides `LICENSE.txt` headed **MICROSOFT SOFTWARE LICENSE TERMS — MICROSOFT .NET LIBRARY**, together with `ThirdPartyNotices.txt` identifying separately licensed components. Those supplier terms and notices apply to that distribution; the project's MIT license does not replace them. For another installed distribution, use the license and notice files supplied with that distribution.

Upstream source projects include [dotnet/runtime](https://github.com/dotnet/runtime) and [dotnet/sdk](https://github.com/dotnet/sdk). Source-repository licenses and notices should not be treated as a blanket replacement for the terms accompanying a particular SDK or runtime distribution.

## Direct development and test dependencies

These packages are restored for development/testing and are not copied into the CLI's published application output. The following declarations were verified in each package's `.nuspec` file:

| Package | Version | Declared license | Source |
| --- | --- | --- | --- |
| xunit | 2.9.3 | Apache-2.0 | [xunit/xunit](https://github.com/xunit/xunit) |
| xunit.runner.visualstudio | 3.1.4 | Apache-2.0 | [xunit/visualstudio.xunit](https://github.com/xunit/visualstudio.xunit) |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT | [microsoft/vstest](https://github.com/microsoft/vstest) |
| coverlet.collector | 6.0.4 | MIT | [coverlet-coverage/coverlet](https://github.com/coverlet-coverage/coverlet) |

Transitive package versions are recorded in `tests/Mxc.GitHubRunner.Tests/packages.lock.json`; their individual NuGet license and notice files continue to apply.
