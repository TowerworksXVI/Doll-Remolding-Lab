# Remold Core — test suite

Automated tests for `Remold.Core` (the app's data layer). xUnit, .NET 10.

```
dotnet test tests/Remold.Core.Tests
```

## Ground rules

- **No real game data, ever.** Every fixture is synthetic and generated at test time: obfuscated
  UnityFS headers built from the known-plaintext magic, `Table/*.bytes` hand-encoded with an
  independent protobuf writer, mesh channels built as plain `float[]`. Nothing copyrighted is checked
  in or produced.
- **Spec-first.** Expected values are derived independently of the implementation — the known-plaintext
  magic for the bundle obfuscation, the protobuf wire format, the outfit ID schemes and naming
  convention — not by reading the code back to itself.
- **The decoder is tested against an independent encoder.** `Support/Pb.cs` is a from-scratch
  protobuf *writer*; the production `Tables/Protobuf.cs` is the *reader*. A bug in one can't mask a
  bug in the other.
- **Synthetic-reachable surface only.** Anything that needs a real UnityFS bundle from the game is out
  of the synthetic unit suite.

## Coverage (foundational data-layer areas)

| Area | File | What's checked |
|---|---|---|
| Bundle obfuscation | `BundleObfuscationTests` | known-plaintext key recovery, XOR symmetry, exact `0x8000` extent, `IsPlain`, deobfuscate no-op on plain, too-small + header-mismatch rejects, magic pin, and `BundleReader.DeobfuscateFile` on a real >32 KB file with markers either side of `0x8000` |
| Bundle segments | `BundleSegmentsTests`, `BundleReaderSyntheticTests` | chain-walk segment enumeration, per-segment deobfuscation (a mid-chain obfuscated segment), foreign/truncated-tail reporting, and each segment as a standalone readable bundle carrying its own self-declared logical name |
| Protobuf reader | `ProtobufTests` | varint / fixed32 / fixed64 / len / sub / repeated / packed decode, tag math, repeated-scalar, sorted field numbers, bad-wire-type throw, non-UTF-8 → null |
| Table + roster | `TableRosterTests` | `TableFile.ReadRows` (header + count skipped), `GameDatabase` roster read, malformed-row skip, name sort, the base / alt / dorm ID schemes, summon membership + stem-prefix ownership (and the loud failure when the summon table is missing), stem + mesh-prefix, case-insensitive `FindCharacter`, `FromGameDir` resolution, enemy-roster grouping/name voting |
| Display + friendly names | `DisplayNamesTests`, `FriendlyNamesTests` | the localized display-name join (character / outfit / summon), the token fallback for a nameless key, and the render-time key→label helper |
| Mesh names | `MeshNameTests` | `_lod`/`_lodm` tail stripping, prefix removal, case-insensitivity, inner-digit retention, `_Dorm`/`_Fight` variant retention |
| Outfit layout | `OutfitLayoutTests` | shared-vs-modular split, `P<n>_` variant parse, humanized labels, natural sort, empty input |
| Game paths | `GameInfoTests`, `GameLocatorTests` | `BundleDir` resolution + throw, catalog-version parse + `unknown` fallback, install validation (both sentinels), `libraryfolders.vdf` parse, Steam-common expansion |
| GFF manifest | `GffManifestTests` | structural validation at Read, name-keyed locate, loud refusals (junk / wrong seed / duplicate names / out-of-file stub) |
| Catalog key | `CatalogIndexTests` | `CatalogIndex.KeyForAddress` — MD5 of the UTF-16-LE address, dash-hex, plus the scene-suffix form |
| Blacklist | `RosterBlacklistTests`, `BuildBlacklistTests` | the child-NPC content policy: the silent roster-side predicate, and the build-side predicate plus one refusal per enforcement funnel (subject, tier name, stock texture) |
| Settings | `LabSettingsTests` | author round-trip, graceful defaults, null-member coalesce, recent-list front/dedup/cap, forward-compat unknown keys |
| Blender bridge | `BlenderBridgeTests`, `BlenderSendWatcherTests` | Blender/editor discovery, the sidecar write-complete sentinel, glb send-back import to Unity space + node-transform flag, and the watcher's failure seam |
| Synthetic bundle read | `BundleReaderSyntheticTests` | `BundleReader.ListAssets`/`GetTexture` over a from-scratch UnityFS bundle (`Support/SyntheticBundle.cs`). Fixture: solid-colour RGBA32 textures, hand-authored type trees, no game data |

The later suites (workbench, materials, export round-trips) document themselves in their file headers.

## Synthetic AssetsTools bundle fixtures (`Support/SyntheticBundle.cs`)

`Support/SyntheticBundle.cs` builds a plain UnityFS v7 bundle from NOTHING — no game data, no
`classdata.tpk` — carrying hand-authored Texture2D and Mesh assets (a from-scratch type tree per
class; `AssetFileInfo.Create` accepts a null ClassDatabase once the type is registered in the file's
tree list). This covers the thin AssetsTools type-tree adapters without a real bundle:

- `BundleReader.ListAssets` / `GetTexture` + `UnityMesh.Decode` — the vertex/index byte codec beneath
  is independently covered by the mesh-codec tests (`DecodeRaw`); `DeobfuscateFile` by the boundary
  test above.
- The prefab/material/mesh fixtures (`BuildPrefab`/`BuildOneMaterial`/`BuildOneMesh`) also drive the
  recipe-exact export + renderer-first texture route and the workbench subject build.
