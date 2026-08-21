# Move to New PC

A single portable Windows program whose only job is moving user profile files from an old PC
to a new one. Not a backup tool, not a sync tool, not a cloud product.

Run it on the old PC, run it on the new PC, move the files. Nothing to install.

**Current state: milestones M0–M2 are built. The network transports (M3–M4) and the offline
package (M6) are not.** Right now the app can enumerate user profiles, let you choose what to
move, and copy it into a folder — a USB disk, an external drive — with full verification and
reporting. Read [`docs/COMPAT.md`](docs/COMPAT.md) before trusting any of it: **none of this
has been executed on Windows yet.**

---

## What it does today

```
[Start]  This is my OLD PC
   |
[Transport]  Copy into a folder on this PC   (LAN / cable / offline package: M3, M6)
   |
[Select]  User accounts with live size estimates
          Advanced: tick individual folders; optionally offer application data
   |
[Options] Collision policy, SHA-256 verification, cloud files, EFS, hidden/system, dry run
   |
[Destination] Folder picker with a free-space check and an overlap check
   |
[Transfer]  Byte progress, throughput, ETA, live skip list, pause and cancel
   |
[Report]  Totals plus every skipped and failed item, each with its reason
```

### What gets moved

- **Tier A (on by default)** — Desktop, Documents, Downloads, Pictures, Music, Videos,
  Favorites, Links, Contacts, Saved Games, Searches. Resolved per user from
  `HKU\<SID>\...\User Shell Folders`, so a Documents folder redirected to `D:\` is found
  rather than guessed at. For users who are not signed in, their `NTUSER.DAT` is mounted with
  `RegLoadKey` and **always** unmounted again.
- **Tier B (off by default, one checkbox)** — a curated allow-list, never all of AppData:
  Chrome / Edge / Brave `User Data`, Firefox and Thunderbird profiles, Outlook `.pst`/`.ost`,
  Sticky Notes (legacy `.snt` and modern `plum.sqlite`), Office signatures and templates.
  Entries that do not exist on the machine are not offered. Each one carries an honest note
  about what will and will not survive the trip.
- **Tier C (advanced)** — any folder you add, plus glob/extension/size/date filters.

### What it refuses to do

- **Never copies ACLs or ownership.** The account IDs from the old PC are meaningless on the
  new one; carrying them across is how migrations end up with files nobody can open. The
  destination's own inherited permissions apply. Timestamps and the
  archive/hidden/read-only attributes are preserved; nothing else is.
- **Never follows junctions or symlinks.** Vista+ profiles are full of compatibility
  junctions that point at their own ancestors. They are logged, not recreated.
- **Never silently overwrites.** Default collision policy is Skip.
- **Never silently drops anything.** Every skip carries a reason and appears in the report.

---

## Building

### On Windows

```
build.cmd            # Release by default; build.cmd Debug also works
```

Uses a modern MSBuild if Visual Studio is present (fetching the net40 reference assemblies
automatically), otherwise falls back to the in-box `.NET Framework 4.0` MSBuild, which needs
nothing extra. The solution also opens and builds in Visual Studio 2010 as-is.

### On Linux or macOS

```
./build.sh
```

MSBuild cannot build a `v4.0` project off Windows, so `build.sh` drives the Roslyn compiler
from the modern .NET SDK directly with `-nostdlib -langversion:4` against the
`Microsoft.NETFramework.ReferenceAssemblies.net40` package. The output is a genuine
`PE32 executable (GUI) ... for MS Windows` with metadata runtime `v4.0.30319` and the admin
manifest embedded — the same binary MSBuild would produce.

Both paths produce, in `build/`:

| File | What it is |
|---|---|
| `MoveToNewPC.exe` | The product. One file. Copy it to a USB stick and run it. |
| `MoveToNewPC.Tests.exe` |Not shipped.Self-hosted test harness (55 tests). Run it on Windows. |
| `MakeTestTree.exe` | Generates a deliberately nasty folder tree to test against. |
| `MoveToNewPC.Core.dll` | Not shipped. Built **without** a WinForms reference purely to prove Core stays headless. |

The shipped EXE compiles the Core sources in rather than referencing `Core.dll`, because
"one portable EXE, no install" means there must be nothing to lose alongside it.

---

## Testing

```
build\MoveToNewPC.Tests.exe            # everything (Windows, elevated)
build\MoveToNewPC.Tests.exe Walker     # filter by group or test name
```

```
./tools/verify-pure.sh                 # the 29 portable tests, runnable anywhere
```

`verify-pure.sh` compiles the Win32-free subset of Core against `net10.0` and runs it. It
exists because on a non-Windows build machine it is the only way to *execute* any of this
code — it covers the glob matcher, the manifest escaping, and the hostile-path rejection.

To exercise the engine against the cases that break migration tools:

```
build\MakeTestTree.exe C:\mtnpc-test --big --lock
```

That builds long paths past `MAX_PATH`, a self-referencing junction, Unicode and RTL names,
reserved device names (`CON`, `PRN.txt`), trailing dot/space names, a 5 GB sparse file, and
optionally holds a file open with no sharing so the locked-file path can be tested against a
real lock.

---

## Repository layout

```
MoveToNewPC.sln
build.sh / build.cmd          Linux+macOS / Windows builds of the same sources
src/MoveToNewPC.App/          WinForms UI, app.manifest, entry point
src/MoveToNewPC.Core/         file engine, profile discovery, manifests, reporting
    Native/                   the entire Win32 P/Invoke surface
    IO/                       \\?\ path layer, walker, receiver-side path validation
    Profiles/                 ProfileList, SID lookup, hive mounting, shell folders
    Selection/                tier catalogues, exclusion rules, filters, size calculator
    Manifests/                streamed manifest format and the resume journal
    Transfer/                 scan engine, transfer engine, sinks
    Reporting/                text and HTML reports
tests/MoveToNewPC.Tests/      self-hosted harness (no NUnit, no NuGet)
tools/MakeTestTree/           the nasty-tree generator
tools/verify-pure.sh          runs the portable tests on a non-Windows machine
docs/PROTOCOL.md              manifest format, journal format, planned wire protocol
docs/COMPAT.md                what is verified, what is not, and known limitations
```

`MoveToNewPC.Core` has no reference to WinForms and never will — it has to stay testable
headlessly and reusable by the offline-package mode.

---

## Why the constraints look like this

| Constraint | Reason |
|---|---|
| .NET Framework 4.0 | Newest runtime that installs on Vista SP2; the in-box 4.x runtimes on Windows 8–11 run 4.0-targeted assemblies unchanged. 3.5 is absent by default on Windows 10/11; .NET Core/5+ excludes Vista and 7. |
| C# 4, no `async`/`await` | Follows from the runtime. Enforced by `-langversion:4`, so it is a compiler error rather than a code-review convention. Worker threads plus `Control.BeginInvoke` throughout. |
| WinForms, native controls | Must look like a Windows utility on every OS from Vista to 11. `SystemColors` and `SystemFonts` everywhere, no custom-drawn chrome, no `.resx` anywhere — which also keeps the build a plain `csc` invocation. |
| `\\?\` path layer | Not optional. `System.IO` on .NET 4.0 rejects paths over `MAX_PATH` in managed code before the syscall happens, and real profiles blow past 260 characters constantly. |
| Always elevated | Reads other users' profiles and mounts their registry hives. `requireAdministrator` in the manifest; never self-elevates at runtime. |
| Zero runtime dependencies | BCL and P/Invoke only. It has to run from a USB stick on a machine with nothing installed. |
| AnyCPU | 64-bit process on a 64-bit OS, so the WOW64 filesystem redirector never applies. |

Where a constraint forced an ugly implementation, there is a one-line comment in the source
saying which constraint forced it.

---

## Next steps

1. **Run `MoveToNewPC.Tests.exe` on a real Windows machine.** 26 of the 55 tests have never
   executed. That is the highest-value thing anyone can do to this repository right now.
2. M3 — LAN transport: UDP discovery, ECDH + pairing-code handshake, encrypt-then-MAC framing.
   The wire format is already specified in [`docs/PROTOCOL.md`](docs/PROTOCOL.md) §3–4, and the
   file engine already sits behind a single `ITransferSink` seam that the network channel
   plugs into unchanged.
3. M4 — receiver-side account mapping and the firewall rule lifecycle. The resume journal and
   the hostile-path validation it depends on are already built and tested.
