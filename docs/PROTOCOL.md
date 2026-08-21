# Wire and file formats

Status: **M2**. The manifest and journal formats below are implemented and stable.
The network handshake and frame layout (§3, §4) are specified here as the target for M3
and are **not implemented yet**.

---

## 1. Manifest file (`.mtnpc-manifest`)

The manifest is the unit of work *and* the unit of resumability. It is a **streamed,
append-only, line-oriented text format** rather than XML or JSON, for three reasons:

1. A 200 GB profile is millions of entries. Nothing may require holding them all in RAM.
2. The sender must be able to append entries while scanning and the receiver must be able
   to consume them while receiving.
3. A truncated manifest (power cut mid-scan) must still be readable up to the last
   complete line.

### Encoding

* UTF-8, **no BOM**.
* One record per line, terminated by `LF` (`\n`). A trailing `CR` is tolerated on read.
* Fields are separated by a single `TAB` (`U+0009`).
* Field values are escaped: `\` → `\\`, TAB → `\t`, LF → `\n`, CR → `\r`.
  Nothing else is escaped — file names contain arbitrary Unicode and must survive intact.
* Empty optional fields are written as the empty string.

### Record types

The first line is always the signature:

```
MTNPC-MANIFEST<TAB>1
```

where `1` is `TransferManifest.FormatVersion`. A reader that does not recognise the
version must refuse the file rather than guess.

| Tag | Meaning | Fields after the tag |
|-----|---------|----------------------|
| `H` | Header  | `manifestId`, `createdUtc` (ISO-8601 `yyyy-MM-ddTHH:mm:ssZ`), `sourceMachine`, `toolVersion` |
| `U` | User    | `userIndex`, `sid`, `accountName`, `profilePath`, `destinationHint` |
| `R` | Root    | `userIndex`, `rootIndex`, `tier` (int), `sourcePath`, `destinationRelativeRoot`, `label` |
| `D` | Directory | `userIndex`, `rootIndex`, `relativePath`, `attributes`, `ctime`, `atime`, `mtime` |
| `F` | File    | `userIndex`, `rootIndex`, `relativePath`, `length`, `attributes`, `ctime`, `atime`, `mtime`, `sha256` |
| `S` | Skip    | `userIndex`, `rootIndex`, `relativePath`, `reason` (int `SkipReason`), `length`, `detail` |
| `T` | Totals  | `fileCount`, `byteCount`, `directoryCount`, `skippedCount`, `skippedBytes` |

Ordering rules:

* `H` comes first, then all `U` and `R` records, then `D`/`F`/`S` records interleaved,
  then a single `T` record last.
* A `D` record for a directory always precedes the `F` records inside it, so a consumer
  can create directories as it goes without buffering.
* `sha256` on an `F` record is empty at scan time. It is filled in only when the sender
  has actually read the file; the receiver gets the real value on the wire, not from here.

Numeric conventions:

* `attributes` is the Win32 attribute DWORD, decimal, **already masked** to the portable
  set (`READONLY | HIDDEN | ARCHIVE`). ACLs and ownership are never represented — they are
  meaningless on the new machine and copying them creates unreadable files.
* `ctime` / `atime` / `mtime` are **FILETIME ticks** (100 ns since 1601-01-01 UTC),
  decimal. `0` means "unknown, leave the destination's own value alone".
* `length` is decimal bytes and may exceed 2^32.

`relativePath` is always relative to the root's `sourcePath`, uses `\` separators, has no
leading separator, and never contains `.` or `..` segments. The receiver re-validates all
of this regardless of what the sender claims (see §5).

---

## 2. Completion journal (`.mtnpc-journal`)

Written by the **receiver**, next to the destination, so a dropped connection or a reboot
resumes instead of restarting. Append-only, same escaping as the manifest.

```
MTNPC-JOURNAL<TAB>1
M<TAB><manifestId>
C<TAB>userIndex<TAB>rootIndex<TAB>relativePath<TAB>bytes<TAB>sha256
X<TAB>userIndex<TAB>rootIndex<TAB>relativePath<TAB>reason<TAB>detail
```

* `C` = completed and verified. On resume, an entry present as `C` is answered with
  `SinkFileDecision.AlreadyComplete` and never re-sent.
* `X` = permanently skipped or failed; recorded so the final report is accurate across
  resumes. `X` entries **are** retried on a later run — the record exists for reporting,
  not to suppress work.
* `M` binds the journal to one manifest id. A journal whose id does not match the manifest
  being started is ignored (and logged), never silently reused.

Partial files are written as `<name>.mtnpc-part` and renamed onto the final name only
after the hash check passes. Any `.mtnpc-part` file found at the start of a run belongs to
an interrupted transfer and is deleted before that file is retried.

---

## 3. Network handshake (M3 — specified, not yet implemented)

1. Receiver generates a random 6-digit pairing code, displays it, starts listening.
2. Both sides perform ECDH (`ECDiffieHellmanCng`, P-256 via CNG — available from Vista,
   which is why this is done at the application layer and not with `SslStream`;
   Vista has no TLS 1.2 at all).
3. Session keys are derived with PBKDF2 (`Rfc2898DeriveBytes`) over a transcript hash
   binding **both public keys, both machine names, the protocol version, and the pairing
   code**. Separate keys per direction.
4. Each side proves knowledge of the derived key with HMAC-SHA256 over the transcript
   before any data flows. A wrong pairing code fails here. This is the only thing
   stopping an active man-in-the-middle and is not optional.
5. Three failed attempts tear the listener down; the operator must restart pairing with a
   fresh code.

## 4. Channel (M3)

* AES-256-CBC **encrypt-then-MAC** with HMAC-SHA256. Not GCM: `AesGcm` does not exist on
  this runtime and cannot be relied on down to Vista.
* Fresh random IV per frame. A monotonic sequence number is inside the MAC'd data to block
  replay and reordering.
* Length-prefixed frames with a hard maximum size; oversized frames are rejected before
  any allocation.
* Constant-time comparison for all MACs and for the pairing code
  (`Format.ConstantTimeEquals`).
* ~30 s handshake timeout, idle timeout, exactly **one** active session; additional
  connections are refused, not queued.
* UDP discovery beacons carry only protocol version, machine name and port. No usernames,
  no paths, no file lists, no code.

## 5. Receiver-side path validation (assume the sender is hostile)

Applied to every incoming `relativePath` before it is used, in `PathValidation`:

* Reject absolute paths, drive letters, UNC prefixes, leading separators.
* Reject `..` and `.` segments, `:` (alternate data streams), and NUL or other control
  characters.
* Reject MS-DOS device names (`CON`, `PRN`, `NUL`, `LPT1`, …) in any segment.
* Reject trailing dots and spaces on a segment (they resolve to a different file on
  Windows than the name suggests).
* Canonicalise, then verify the resolved path is still inside the mapped destination root.
  Reject if not.
* Enforce the declared file length and the manifest total; abort on overrun.

Nothing received is ever executed, registered, or opened.
