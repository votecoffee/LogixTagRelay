# Release verification

The six packages remain previews. Release verification checks package quality and
consumer deployment without contacting a PLC. Studio 5000 and live-controller
acceptance are tracked separately in [VALIDATION.md](VALIDATION.md).

## Build and verify

On Windows x64, install .NET SDK 8 or later and the native build prerequisites in
the root README. Use a clean, committed checkout with the pinned OpENer submodule:

```powershell
./scripts/verify.ps1
./scripts/verify-release.ps1
```

The first command runs the existing offline, emulator, browser and sample checks.
The second rebuilds with `ContinuousIntegrationBuild=true`, packs all six NuGets,
verifies their contents and symbols, exercises rejection cases, and publishes and
runs a consumer restored into an empty package cache outside the repository.
It requires committed source so Source Link can resolve the packaged bytes.
Push that commit before distributing packages to other developers.

To test both installed runtime families, or reuse a native bridge already built
from the same pinned source:

```powershell
./scripts/verify-release.ps1 -SkipNativeBuild -ConsumerFrameworks net8.0,net9.0
```

Each consumer targets its requested framework and uses `LatestPatch` roll-forward;
a .NET 8 check cannot silently run on .NET 9 or 10. Both frameworks require the
corresponding runtime and a compatible SDK. A successful local isolated-cache run
is evidence for this machine; it does not establish installation on a fresh OS.

The manually dispatched **Release checks** workflow runs the full checks and both
consumers on a fresh `windows-2022` hosted runner. Its VM has the VS native toolchain;
it is not a bare Windows installation. It uploads packages and verification receipts
and never publishes to NuGet. There are no automatic push/PR/scheduled triggers.

## XML documentation and source debugging

Every public library builds with XML documentation and warnings treated as errors.
Missing public documentation fails with `CS1591`. Generated contract types also
include XML summaries; the package consumer compiles its separate contract library
with documentation enforcement enabled.

The five public library packages contain their XML files and have matching portable
PDB `.snupkg` companions. The MSBuild generator package embeds its own portable PDB
in its executable DLL, and includes XML documentation for its bundled core and
Design libraries. It does not expose a `lib/` assembly or an empty symbol package.

Source Link is supplied by the .NET 8+ SDK. Metadata points at immutable GitHub
commits, including the pinned native submodule. C# sources use LF through
`.gitattributes` so compiler checksums match Git source bytes on Windows as well.
Generated, untracked compiler sources are embedded in the PDB. The release checker
verifies DLL/PDB identity, portable source paths, commit-pinned URLs and document
checksums against committed or embedded source bytes. Ordinary source uses
SHA-256; upstream regex generators can record SHA-1, which is checked using the
algorithm declared by the document. Package-file hashes always use SHA-256.

For a local feed, extract library PDBs from the `.snupkg` files to a debugger symbol
directory. Ordinary folder feeds do not provide a NuGet symbol server. Once packages
and symbols are published to a compatible server, consumers can use that server and
Source Link for source debugging. See the upstream [Source Link guidance](https://github.com/dotnet/sourcelink)
and [NuGet symbol packaging documentation](https://learn.microsoft.com/en-us/nuget/create-packages/symbol-packages-snupkg).

## Public API compatibility

The five public libraries use the private build dependency
`Microsoft.CodeAnalysis.PublicApiAnalyzers` 3.3.4, compatible with the .NET 8 SDK.
It is not a consumer dependency. Their `PublicAPI.Shipped.txt` files record the
existing preview API, including reference nullability and optional parameter defaults.
Normal builds reject removed shipped members (`RS0017`) and undeclared additions or
signature changes (`RS0016`). No suppression baseline is used.

Review intentional additions in `PublicAPI.Unshipped.txt`. At a release, move reviewed
entries to the shipped file. Do not regenerate the shipped file merely to clear an
unexpected compatibility failure. The new optional `TagNode.Description` property is
an additive member; the original constructor and deconstruction signatures remain.

SDK package validation also runs for every public library during `dotnet pack`.
To compare the packed binaries with a previous version, supply its package version
and feed. For the unpublished 0.8 preview, use the retained local packages:

```powershell
./scripts/verify-release.ps1 -SkipNativeBuild `
  -BaselineVersion 0.8.0-preview.1 `
  -BaselinePackageDirectory ./artifacts/packages
```

For published releases the baseline can restore from nuget.org. Package baseline
validation checks binary compatibility; the source API files also make nullability
and default-value changes visible during review. The custom generator package is
validated through actual generation and compiler diagnostics rather than a library
API comparison. See [SDK package validation](https://learn.microsoft.com/en-us/dotnet/fundamentals/package-validation/overview).

## Consumer and rejection checks

`scripts/verify-package-consumer.ps1` copies only the sanitized consumer fixture to a
new temporary directory. It uses a separate NuGet package/HTTP cache, clears inherited
package feeds, blocks ancestor MSBuild customization and restores the exact six-package
version from the selected local feed plus nuget.org for public dependencies.
It publishes a framework-dependent Windows x64 application and verifies:

- Build-generated contracts in a separate library, copied configuration and compiled
  IntelliSense documentation from tag comments and UDT member defaults.
- Explicit dirty-member writes, custom STRING, rectangular arrays, exact PLC time,
  and silent unchanged-value reads.
- Managed implicit baseline exchange and a separate explicit-only heartbeat.
- The packaged JavaScript engine, typed faults and native ABI 2.
- Startup/shutdown of the owned native worker without binding EtherNet/IP ports.
- Rejection of an invalid contract with `LTR001`.

`scripts/verify-release-guards.ps1` checks that API removal, unreviewed additions and
missing documentation fail compilation. It also rejects packages with missing XML,
mismatched symbols or a missing native bridge. Positive fixtures run first to ensure
the negative results are not caused by a broken test environment.

Logs and isolated caches are retained in the reported temporary directories, including
failed runs. Receipts and package SHA-256 hashes go under ignored `artifacts/packages`.
No credentials, customer exports, cache contents or machine paths belong in source
control. Verification does not require a NuGet publishing key.

## Deployment boundary

Libraries target `net8.0`. The implicit adapter requires Windows x64, the native MSVC
runtime dependencies and a framework-dependent app with its runtimeconfig/deps files
and the `dotnet` host available. The worker launcher does not support single-file or
self-contained publishing. Native bridge loading and the worker probe detect missing
runtime prerequisites; neither proves PLC networking or controller interoperability.

Keep the preview designation until the documented native/controller acceptance is
complete. Package signing and publication require a chosen distribution destination
and publisher credentials; neither is performed by these verification scripts.
