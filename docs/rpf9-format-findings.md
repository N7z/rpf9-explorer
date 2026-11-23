# RPF9 archive index research

The protected headers of three local GTA 6 RPF9 samples were decoded in a separate research tool. Two implementations agreed byte for byte on the resulting headers. One implementation was checked against 64 block transform inputs from an isolated emulation run.

| Sample | Header bytes | Entry slots |
| --- | ---: | ---: |
| `appdata0.rpf` | 864 | 2 |
| `common.rpf` | 38,272 | 418 |
| `packs.rpf` | 17,744 | 172 |

The header uses three transform stages in these samples. The first stage chains across two metadata spans. It reveals the descriptor used for the remaining two spans, each of which starts with fresh chaining state.

The decoded index declares six record tables. Each entry has indices into those tables, and different entries can share a table record. Each index either points to a record inside its declared table or uses a sentinel for a missing record. Every index and record range fit its declared bounds across the three sample headers.

The desktop app currently discovers supported payloads by scanning archives. This research is separate from the app. Original filename resolution, member extraction through the decoded index, archive authentication, and archive rebuilding remain open.
