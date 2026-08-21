# Compatibility and verification status

**Read the first section before trusting anything else in this file.**

---

## 1. What has actually been verified, and what has not

This project was developed in a Linux container. That has one consequence that matters more
than everything else in this document:

> **No part of this application has been executed on Windows.**
> Not on Vista, not on 11, not once.

What *has* been done is set out below. Nothing is claimed beyond it.

### Verified by execution

| What | How | Result |
|---|---|---|
| Glob matcher, incl. multi-star backtracking | 6 tests, run on .NET 10 via `tools/verify-pure.sh` | pass |
| Hex encode/decode, constant-time compare, byte/duration formatting | 5 tests, same runner | pass |
| Manifest field escaping round-trip (tabs, newlines, backslashes, Unicode) | 4 tests, same runner | pass |
| `LongPath` string handling: `\\?\` prefixing, Combine, relative-path containment, reserved names | 6 tests, same runner | pass |
| `PathValidation` rejection of hostile relative paths | 8 tests, same runner | pass |

`tools/verify-pure.sh` compiles the subset of Core that touches no Win32 API against
`net10.0` and runs it. It is the only way to get real test results without a Windows
machine. **29 tests, all passing.**

### Verified by compilation only

| What | How | Result |
|---|---|---|
| Whole product targets .NET Framework 4.0 | Roslyn `-nostdlib -langversion:4` against `Microsoft.NETFramework.ReferenceAssemblies.net40` | 4 assemblies, metadata runtime `v4.0.30319` |
| Output is a real Windows binary | `file build/MoveToNewPC.exe` | `PE32 executable (GUI) ... for MS Windows` |
| Language level is genuinely C# 4 | `-langversion:4`; `async`, `$"…"` and `?.` each hard-error | enforced by the compiler, not by review |
| Admin manifest is embedded | byte scan of the `.rsrc` section | `requireAdministrator` present |
| `supportedOS` GUIDs Vista → Win 11 | byte scan | Vista and Win10/11 GUIDs present |
| `dpiAware` / `dpiAwareness` / Common-Controls 6 | byte scan | present in `MoveToNewPC.exe` |
| Core is headless | Core.dll compiled with **no** WinForms/Drawing reference on the command line | no UI reference in the output |
| Zero runtime NuGet dependencies | reference list in the built assemblies | `mscorlib`, `System`, `System.Core`, `System.Security` only (plus WinForms/Drawing in the EXE) |

### Not verified at all

Everything below compiles and is written to the documented Win32 contracts, and **none of it
has been observed working**:

- Every P/Invoke in `Native/NativeMethods.cs` — signatures, marshalling, struct layout.
- `\\?\` long-path enumeration and copying (`DirectoryWalker`, `NativeFile`).
- Reparse-point detection and the refusal to descend into junctions.
- Cloud-placeholder detection (`FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS` and friends).
- Registry hive loading/unloading (`RegLoadKey` / `RegUnLoadKey`) and privilege enabling.
- `ProfileList` enumeration and `LookupAccountSid` resolution.
- User Shell Folders resolution, including the per-user `%USERPROFILE%` expansion.
- Timestamp and attribute preservation.
- Every screen of the WinForms UI: layout, DPI behaviour, theming, threading.
- The `.mtnpc-part` → rename → hash-verify path and resume from the journal.

`tests/MoveToNewPC.Tests/WindowsTests.cs` contains **26 tests covering exactly this list**.
They are compiled into `build/MoveToNewPC.Tests.exe` and have never been run.

**Running that EXE on a real Windows machine is the next thing that should happen to this
project.** It is a single portable executable; it needs no install.

---

## 2. How to verify it yourself

On any Windows machine (elevated — the manifest forces the prompt):

```
MoveToNewPC.Tests.exe
```

Expect 29 portable tests plus 26 Windows tests. Anything that cannot run (e.g. `mklink`
unavailable, so no junction can be created) reports `SKIP` with a reason rather than
silently passing.

To exercise the file engine against deliberately hostile input:

```
MakeTestTree.exe C:\mtnpc-test --big
MoveToNewPC.exe          (Old PC -> copy into a folder -> add C:\mtnpc-test)
```

The report must mention the junctions as not-followed, the over-4 GB sparse file, the
reserved names (`CON`, `PRN.txt`), and the trailing-dot/space names. If any of those are
missing from the report, the engine is lying and that is a bug.

---

## 3. Target matrix

Support is *designed for* the whole range below. The Status column says what is known, not
what is hoped.

| OS | Arch | .NET 4.0 available | Status |
|---|---|---|---|
| Windows Vista SP2 | x86, x64 | Installable (not in-box) | **Untested.** Highest-risk target: see §4. |
| Windows 7 SP1 / Server 2008 R2 | x86, x64 | Installable; 4.8 common | **Untested.** |
| Windows 8 / Server 2012 | x86, x64 | 4.5 in-box, runs 4.0 targets | **Untested.** |
| Windows 8.1 / Server 2012 R2 | x86, x64 | 4.5.1 in-box | **Untested.** |
| Windows 10 | x86, x64 | 4.6+ in-box | **Untested.** |
| Windows 11 / Server 2016+ | x64, ARM64 | 4.8 in-box | **Untested.** ARM64 runs the x86/x64 image under emulation; not considered. |

The runtime choice is deliberate: .NET Framework 4.0 is the newest runtime that installs on
Vista SP2, and the 4.x runtimes shipped in-box on Windows 8 through 11 execute
4.0-targeted assemblies unchanged. Targeting 3.5 would have been worse (not present by
default on Windows 10/11); .NET Core or 5+ would have excluded Vista and 7 entirely.

---

## 4. Known and suspected limitations

### Confirmed by design

- **ACLs and ownership are never copied.** Deliberate. The SIDs from the old machine are
  meaningless on the new one, and copying them produces files nobody can open. Destination
  inheritance applies instead. Timestamps and the archive/hidden/read-only attributes are
  preserved; nothing else is.
- **Alternate data streams are ignored.** Almost always `Zone.Identifier`.
- **Junctions and symbolic links are recorded, not recreated.**
- **EFS-encrypted files** are skipped by default. Including them produces files that will
  not decrypt on the new PC without also moving the certificate. The UI warns in red before
  the transfer, not after.
- **Cloud placeholders** are skipped by default rather than hydrated, because reading one
  can pull gigabytes over a metered connection.
- **No VSS snapshotting.** Explicitly out of scope for v1: a file locked by a running
  application is retried, then skipped with a reason. This is the single biggest reason a
  file will not copy, and it is why the report exists.

### Suspected risks on specific Windows versions

These are engineering judgements, not observations:

- **Vista, per-monitor DPI.** Vista has no per-monitor DPI at all. The manifest declares
  `dpiAware=true` plus `dpiAwareness=PerMonitorV2, PerMonitor, System`; older parsers ignore
  the element they do not understand and fall back to system DPI. `dpiAware="true/pm"` was
  deliberately **not** used, because Windows 7 does not understand that value and can drop
  the process to DPI-unaware, which is worse everywhere.
- **Vista, cloud attributes.** `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS` did not exist before
  Windows 10. On older systems the bit is simply never set, which is correct behaviour —
  there is no OneDrive files-on-demand there either.
- **`SHA256Cng`.** Chosen over `SHA256Managed` because the managed implementation throws on
  machines with the FIPS policy enabled, which is common on corporate builds.
  `HashFactory` falls back CNG → CryptoServiceProvider → managed. The fallback order is
  compiled but has never been exercised.
- **Registry hive unloading.** `RegUnLoadKey` fails while any key in the hive is open. The
  code closes its keys, forces a GC, and retries five times. If it still fails it logs an
  error loudly — a leaked hive can stop that user logging in. **This is the most dangerous
  code path in the application and it has never been run.**
- **`Environment.SpecialFolder.ProgramFilesX86`** on a 32-bit OS resolves to the same place
  as `ProgramFiles`. Harmless; both are exclusion roots.
- **AnyCPU and WOW64.** Built AnyCPU with no `Prefer32Bit` (which does not exist on 4.0), so
  on a 64-bit OS the process is 64-bit and the filesystem redirector does not apply.

### Deviation from the specification, deliberate and flagged

§6 of the build prompt asks to filter ProfileList entries with "a non-zero `Special`/`Flags`
value". `ProfileEnumerator` filters on `Special != 0` unconditionally, but filters on
`Flags != 0` **only** for SIDs that are not of the form `S-1-5-21-*`. Some entirely normal
roaming and mandatory profiles set `Flags`, and hiding a real user's files is a far worse
failure than showing one extra row. Everything filtered is counted and listed with its
reason on the selection screen, so nothing is hidden either way.

---

## 5. Milestone status

| Milestone | State |
|---|---|
| **M0** Skeleton, manifest, logging, role picker | Built. Not launched on any Windows version. |
| **M1** Profile discovery, hive loading, shell folders, background sizing | Built. Untested. |
| **M2** Long-path engine, walker, manifest, copy + hash + resume, dry run, report | Built. Pure logic tested; Win32 paths untested. |
| **M3** LAN transport, ECDH + pairing handshake, encrypt-then-MAC | **Not started.** Wire format specified in `docs/PROTOCOL.md` §3–4. |
| **M4** Receiver mapping, resume UI, firewall rule lifecycle | **Not started.** Resume machinery and receiver path validation already exist and are tested. |
| **M5** Advanced mode: tri-state lazy folder tree, filters, exclusion editor | **Partial.** Per-folder checkboxes, the Tier B allow-list, the filter engine and the exclusion rule engine all exist; the tri-state lazy tree and the editor UI do not. |
| **M6** Offline package, manual IP helper, compat matrix | **Not started**, except this document. |
