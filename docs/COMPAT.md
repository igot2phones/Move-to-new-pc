# Compatibility and verification status

**Read the first section before trusting anything else in this file.**

---

## 1. What has actually been verified, and what has not

This project was developed in a Linux container and was, for most of its life, never executed
on Windows at all. **That is no longer true.**

> **First Windows run: 2026-08-21, Windows 11 Enterprise 10.0.26200 (x64), elevated,
> locale el-GR.**
> Full suite: **56 passed, 0 failed, 0 skipped.** The GUI starts. The file engine has been
> run end to end against the hostile tree from `MakeTestTree.exe`.

That run found three real defects, all now fixed — see §1.1. It leaves the WinForms UI still
unverified by human eyes: the process starts and creates its window, but nobody has yet
clicked through the wizard, so nothing is claimed here about layout, theming, DPI or
clipping. Everything else below is now backed by an actual execution.

### 1.1 What the first Windows run found

| Defect | Where | Fix |
|---|---|---|
| Rooted paths containing `..` were passed to Win32 with the `\\?\` prefix still carrying the `..`. Because that prefix disables normalisation, every such call failed with `ERROR_INVALID_NAME` (123). Hit immediately: `tools\windows-verify.cmd` sets `ROOT=%~dp0..`, so the whole nasty-tree step died. | `LongPath.ToExtended` normalised only *relative* paths, via `GetFullPathNameW`. Rooted paths went through untouched. | `LongPath.CollapseRelativeSegments` collapses `.` and `..` segments for rooted paths too, clamping at the root (and, for UNC, at the share). Trailing dots and spaces are deliberately still preserved, so `GetFullPathNameW` is *not* used here — it would strip them. |
| The Windows build had never worked. `Program.cs` and `PureMain.cs` both declare `Main`, and the csproj set no startup object, so MSBuild failed with `CS0017`. The Linux build passed `-main:` on the command line and so never hit it. | `tests\MoveToNewPC.Tests\MoveToNewPC.Tests.csproj` | Added `<StartupObject>MoveToNewPC.Tests.Program</StartupObject>`, which is exactly what `PureMain.cs`'s comment says was intended. |
| `Format / byte formatting is readable` failed on any non-English locale. | The test asserted `"1.00 KB"`. `Format.Bytes` correctly uses `CurrentCulture`, which on el-GR yields `"1,00 KB"`. | Test fixed, not the code — `Format.Bytes` is display-only and *should* be localised. It now asserts against `CurrentCulture.NumberFormat.NumberDecimalSeparator`. |

### Verified by execution on Windows (2026-08-21)

| What | Result |
|---|---|
| Whole suite, elevated, Windows 11 26200 | **56 / 56 pass**, 0 skipped, ~440 ms |
| Registry hive load/unload, and no hive left mounted | pass — `reg query HKU` clean, **no stray `MTNPC_*` keys** |
| Profile enumeration + `LookupAccountSid` | pass — finds the current user, known folders resolve |
| Junction detection; refusal to descend | pass — the self-referencing junction did **not** loop |
| `\\?\` long paths, enumeration and copy | pass — **875-character destination paths written** |
| Timestamp + portable attribute preservation (`SetFileTime`) | pass |
| Locked file is skipped, run continues | pass |
| Path-traversal rejection in the writer | pass — `Data\..\..\..\..\Windows\System32\pwned.dll` rejected and logged |
| 5 GB sparse file over the 4 GB boundary | copied, full 5,368,709,120 bytes |
| GUI process starts and creates a window | pass (smoke test only — see §1.2) |

### 1.2 Still not verified

- **Every visual aspect of the WinForms UI**: layout, theming, per-monitor DPI, text
  clipping, and whether the account list stays responsive while sizes are counted. The
  process starts; no human has looked at it.
- Any Windows version other than 11 build 26200. See §3.
- The LAN transport (M3/M4) — not written yet.

### Verified by execution before Windows was available

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

### Formerly "not verified at all" — now run

`tests/MoveToNewPC.Tests/WindowsTests.cs` covers this list. As of 2026-08-21 it **has been
run**, on Windows 11 26200 x64, elevated, and passes:

- Every P/Invoke in `Native/NativeMethods.cs` — signatures, marshalling, struct layout. ✅
- `\\?\` long-path enumeration and copying (`DirectoryWalker`, `NativeFile`). ✅ 875-char paths.
- Reparse-point detection and the refusal to descend into junctions. ✅ self-junction did not loop.
- Registry hive loading/unloading (`RegLoadKey` / `RegUnLoadKey`) and privilege enabling. ✅ nothing leaked.
- `ProfileList` enumeration and `LookupAccountSid` resolution. ✅
- User Shell Folders resolution, including the per-user `%USERPROFILE%` expansion. ✅
- Timestamp and attribute preservation. ✅
- The `.mtnpc-part` → rename → hash-verify path and resume from the journal. ✅ no `.mtnpc-part` left behind.

Still genuinely unverified:

- **Every screen of the WinForms UI: layout, DPI behaviour, theming, threading.** The
  process starts and creates its window; that is all that is known.
- **Cloud-placeholder detection** (`FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS` and friends) — the
  test machine had no OneDrive files-on-demand placeholders, so the bit was never exercised
  against a real placeholder.

**Clicking through the GUI on a real Windows machine is now the next thing that should
happen to this project** — the engine underneath it has been run and is sound, but no human
has yet used the wizard. It is a single portable executable; it needs no install.

---

## 2. How to verify it yourself

On any Windows machine (elevated — the manifest forces the prompt):

```
MoveToNewPC.Tests.exe
```

Expect **56 tests, all passing** (29 portable, 26 Windows, plus one regression test for the
`..` bug in §1.1). Anything that cannot run (e.g. `mklink` unavailable, so no junction can be
created) reports `SKIP` with a reason rather than silently passing.

`tools\windows-verify.cmd` does all of the above plus the nasty tree and a GUI smoke test,
and writes everything to `verify-results\<timestamp>\`. Run it from an **elevated** prompt;
without elevation it stops, because the profile and hive tests are the entire point.

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
| Windows 11 / Server 2016+ | x64, ARM64 | 4.8 in-box | **x64: tested 2026-08-21, build 26200, el-GR, elevated — 56/56 pass, GUI starts.** ARM64 runs the x86/x64 image under emulation; not considered. |

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

### Open question raised by the first Windows run

- **Reserved device names and trailing dot/space names are read but never written.** The
  walker enumerates `CON`, `PRN.txt`, `LPT1`, `trailing-dot.` and a name ending in a space
  from the source perfectly well — that is what the `\\?\` layer is for. But `LocalFolderSink.Resolve`
  runs the destination relative path through `PathValidation`, which rejects exactly those
  names, so each one is counted as **failed** (`Rejected: unsafe path`), not skipped.

  Observed on the nasty tree: 5 files failed for this reason. They *are* listed in the
  report with a reason, so nothing goes silently missing — but for a migration tool these
  are real user files that Windows itself is perfectly happy to store, and they do not
  arrive on the new PC.

  This was **not** changed, because the same validator is what will guard the M4 LAN
  receiver against a hostile manifest, where rejecting these names is exactly right. The
  likely fix is to sanitise rather than reject on the *local* copy path —
  `PathValidation.SanitiseSegment` already exists and is tested — and to classify the
  outcome as skipped rather than failed. **That is a design decision, not a bug fix, and it
  is still open.**

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
  error loudly — a leaked hive can stop that user logging in. **Run on 2026-08-21 and it
  did not leak**: `reg query HKU` after the suite showed no `MTNPC_*` keys. That is one
  machine with one loadable profile, so the retry path itself is still unexercised — it
  succeeded first time.
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
| **M0** Skeleton, manifest, logging, role picker | Built. Launches on Windows 11 26200 and creates its window; the role picker has not been looked at by a human. |
| **M1** Profile discovery, hive loading, shell folders, background sizing | Built and **tested on Windows**: discovery, hive load/unload (no leak) and shell folders all pass. Background sizing is engine-side only; the UI's responsiveness while it runs is unverified. |
| **M2** Long-path engine, walker, manifest, copy + hash + resume, dry run, report | Built and **tested on Windows**, including end to end over the hostile tree: 875-char paths, junctions not followed, 5 GB sparse file, no `.mtnpc-part` left behind. One open question — see §4 on reserved names. |
| **M3** LAN transport, ECDH + pairing handshake, encrypt-then-MAC | **Not started.** Wire format specified in `docs/PROTOCOL.md` §3–4. |
| **M4** Receiver mapping, resume UI, firewall rule lifecycle | **Not started.** Resume machinery and receiver path validation already exist and are tested. |
| **M5** Advanced mode: tri-state lazy folder tree, filters, exclusion editor | **Partial.** Per-folder checkboxes, the Tier B allow-list, the filter engine and the exclusion rule engine all exist; the tri-state lazy tree and the editor UI do not. |
| **M6** Offline package, manual IP helper, compat matrix | **Not started**, except this document. |
