# RPF9 archive index research

The protected headers of three local GTA 6 RPF9 samples were decoded in a separate research tool. Two implementations agreed byte for byte on the resulting headers. One implementation was checked against 64 block transform inputs from an isolated emulation run.

| Sample | Header bytes | Entry slots |
| --- | ---: | ---: |
| `appdata0.rpf` | 864 | 2 |
| `common.rpf` | 38,272 | 418 |
| `packs.rpf` | 17,744 | 172 |

The header uses three transform stages in these samples. The first stage chains across two metadata spans. It reveals the descriptor used for the remaining two spans, each of which starts with fresh chaining state.

The decoded index declares six record tables. Each entry has indices into those tables, and different entries can share a table record. Each index either points to a record inside its declared table or uses a sentinel for a missing record. Every index and record range fit its declared bounds across the three sample headers.

## Reading members from the index

The local research reader now uses the index to find member offsets and sizes. It validated every chunked member in the three root samples against its declared decoded length. Compressed chunks also passed zlib checksum validation.

| Sample | Validated chunked members | Members requiring a counter transform | Nested RPF headers decoded |
| --- | ---: | ---: | ---: |
| `appdata0.rpf` | 2 | 2 | 0 |
| `common.rpf` | 418 | 149 | 0 |
| `packs.rpf` | 113 | 11 | 59 |

The 59 nested headers in `packs.rpf` contain 80,298 entry slots. Those checks covered the child headers and their table bounds. Payload validation covered the 533 chunked members in the root archives.

## Looking up names

The index stores 32-bit and 64-bit hashes of relative paths. Lookup treats recognized extensions as a separate type and normalizes ASCII case and slash direction. Tests against isolated native instructions covered 339 hash comparisons. Seven candidate paths for `common.rpf` matched both hashes and the entry type. Entries without a matching candidate remain identifiable by hash and slot number.

The desktop app still discovers supported payloads by scanning archives. The research reader is separate. Full filename recovery, support for other storage layouts, archive authentication, and archive rebuilding remain open.
