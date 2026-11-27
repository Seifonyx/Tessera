# Search upgrade (2026-11-27)

Goal: make Tessera's search as good as possible. Owner picked all extras.

## Matching (class `Matcher`)
Tiers, lower is better: 0 exact, 1 prefix (also ignoring spaces/punctuation), 2 word-start, 3 initials
("vsc" -> Visual Studio Code, camelCase counts: "ps" -> PowerShell), 4 all words in any order, 5 contains,
6 fuzzy in-order letters, 7 one/two typos. Commands from PATH still need tier 0-1.

## Ranking
rank = tier + source penalty - frecency - learned-pick bonus.
`Picks` (picks.txt, gitignored): remembers which result was launched for each typed query; the same or a
related query lifts that result (up to 3 tiers).

## New result kinds
- Settings pages (ms-settings: catalog with keywords) and Control Panel items (enumerated from the shell).
- Calculator: `12*7`, `2^10`, `sqrt(2)`, `15% of 80`; Enter copies the answer.
- Power/system actions: Lock, Sleep, Hibernate, Restart, Shut down, Sign out, Empty Recycle Bin.
  Only prefix/word-start matches, ranked below apps, so a typo can't shut the PC down.
- Files & folders: Windows Search index, user files (not AppData/junk), own section, newest first.
- Web: last row "Search the web for ..."; URL in settings.ini `WebSearch=` (%s = query).

## Status
Implemented in Tessera.cs; see commit.
