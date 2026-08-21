# v0.6.0 — Browsers, email, and putting files back where they belong

Two things people kept having to do by hand: moving browser and mail data, and then dragging
everything out of one big folder into the right places.

Portable single executable. No installer, no NuGet dependencies. Requires .NET Framework 4.0
or later (in the box on Windows 8 and up; installable on Vista and 7) and runs elevated.

## New: move browser and email data in one click

The account screen now has **Select browsers** and **Select email** buttons. Each ticks
everything of that kind that was actually found on the PC, across every account. Nothing
that is not installed is ever offered.

**Browsers detected** — Chrome, Chrome Beta, Edge, Brave, Vivaldi, Chromium, Opera, Opera GX,
Firefox, Waterfox.

**Email detected** — Outlook data files (`.pst`), Outlook cached mailbox (`.ost`/`.nst`),
Outlook signatures, Thunderbird, Windows Live Mail, eM Client.

Every entry says what the catch is, before you copy rather than after:

- **Saved passwords in Chrome, Edge, Brave, Vivaldi, Opera and Chromium will not work on the
  new PC.** They are encrypted with a key tied to the old Windows account. Bookmarks,
  history and extensions do come across. This is a Windows design decision, not something
  this tool can work around.
- **Firefox and Thunderbird** keep their own profile format and usually restore cleanly,
  saved logins included.
- **Outlook `.ost` files are a cache, not your mail.** They are offered, but flagged: they
  are often many gigabytes and Outlook simply rebuilds them. Your actual mail is the `.pst`.
- **Close the application first.** A file held open by a running browser or mail client is
  skipped and listed in the report with the reason.

## New: put the files back where they belong

The new PC can now merge files into their real folders instead of dropping everything into
one place. On either receiving screen, choose:

**Put them back in their normal places on this PC** — Desktop, Documents, Downloads, Music
and Videos merge into this PC's own folders of the same name. Everything else — custom
folders, browser and mail data, and folders outside that list — goes into a single
`From <old PC>` folder on your Desktop, so nothing is scattered.

**Put everything in the folder I choose** — the previous behaviour, and still the default.
It cannot disturb anything already on the machine, which matters if you have started using
the new PC already.

Two things worth knowing:

- Folders are resolved live, so a PC with Documents redirected to another drive works
  correctly.
- **Only the first account in a transfer is merged into this PC's folders.** Merging two
  people's Documents into one place would lose data, so every other account gets its own
  folder under the Desktop drop instead.
- Files that clash with one already there are left alone unless you change the collision
  policy on the options screen.

## Verified

**71 / 71 tests pass, 0 skipped**, on Windows 11 Enterprise build 26200, x64.

Five new tests cover the restore layout: that exactly the five agreed folders are mapped and
Pictures and Favorites are not, that this PC's folders resolve, that a known folder goes home
while a custom folder lands on the Desktop, that the single-folder layout is unchanged, and
that a second account never merges into the first.

Browser and mail detection was checked against a real profile: it found and correctly
categorised the installed browser and offered nothing that was not there.

Everything from v0.5.0 still passes — the LAN handshake and transfer, the encrypted package
round trip, long paths, junctions, unicode names and the hostile test tree.

## Known limitations

- **Pictures is not one of the five folders put back in place.** Photos land in the Desktop
  folder with everything else. This was deliberate, matching the agreed list; say so if you
  want Pictures added.
- **This build's test run was made with an unelevated harness.** The sources are identical
  and all 71 tests pass, but the shipped `MoveToNewPC.Tests.exe`, which carries
  `requireAdministrator`, has not been launched for this exact build, and neither has the
  GUI. Run `MoveToNewPC.Tests.exe --no-pause` from an elevated prompt to confirm on your own
  machine.
- **Only tested on Windows 11**, and the network transfer only between two processes on one
  machine, not two physical PCs. See `docs/COMPAT.md` §3.
- **No resume UI** for an interrupted transfer. The journal that makes resuming possible is
  written and works; the screen to drive it is not built.
- **Files named `CON`, `PRN.txt`, `LPT1`, or ending in a dot or space are not written.**
  They are read and reported with a reason, never silently dropped.
- **Nobody has clicked through the whole wizard on a high-DPI or scaled display.**
- ACLs, ownership and alternate data streams are never copied. Deliberate — the account IDs
  from the old PC are meaningless on the new one.

## Files

| File | What it is |
|---|---|
| `MoveToNewPC.exe` | The application. Run it on **both** PCs. This is the only file you need. |
| `MoveToNewPC.Tests.exe` | The full 71-test suite. Run elevated with `--no-pause`. |
| `MakeTestTree.exe` | Generates a deliberately hostile test tree, if you want to try it against something nasty. |

Each is self-contained; none of them needs the others.
