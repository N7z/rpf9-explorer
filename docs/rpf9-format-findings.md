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

The 59 nested headers in `packs.rpf` contain 80,298 entry slots. The local reader checked all 485 nested data members and all 79,813 resource payloads. The full resource pass produced 9,722,110,144 decoded bytes. All 143,017 zlib chunks passed checksum validation, all 1,157 raw chunks matched their declared sizes, and every payload matched its declared system and graphics lengths.

The reader also decoded the indexes of 22 standalone RPF9 files, including the animation, audio, level, movie, shader, and texture archives. The 6,534 top-level slots in this survey include 5,150 directly stored members, 1,383 chunked data members, and one chunked resource. All 1,384 chunked payloads passed their length and codec checks.

Of the directly stored members, 797 had Bink movie headers and frame tables that agreed with their indexed lengths. Frame table checks covered offset order, bounds, and the final frame boundary. They did not decode the video frames.

The survey decoded headers for all 4,351 directly stored child archives found in the standalone indexes. Those children contain 1,248,505 entry slots. Full payload checks for nested resources covered the `packs.rpf` children.

## Header signatures

A separate pass verified the signatures of all 22 standalone headers and all 4,351 observed child headers with the RSA-2048 public key embedded in the analyzed build. The format uses PKCS #1 v1.5 with double SHA-256 over selected header ranges after the first transform. Controlled changes to covered bytes failed verification. These checks cover the signed header ranges. The key came from the local executable and its provenance was not independently established.

## Looking up names

The index stores 32-bit and 64-bit hashes of relative paths. Lookup treats recognized extensions as a separate type and normalizes ASCII case and slash direction. Tests against isolated native instructions covered 339 hash comparisons.

Searching executable strings and decoded metadata produced candidates for 5,608 of the 6,534 standalone slots. Of those, 1,378 match both hashes and the entry type, including all 797 indexed movies. Another 4,230 child archive candidates match their available 64-bit hash and entry type. Some script matches identify a name stem and extension family. Original casing and unmatched paths remain unresolved.

The desktop app still discovers supported payloads by scanning archives. The research reader is separate. Full filename recovery, broader nested payload validation, and archive rebuilding remain open.
