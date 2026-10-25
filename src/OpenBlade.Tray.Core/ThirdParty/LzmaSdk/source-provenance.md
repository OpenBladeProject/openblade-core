# LZMA SDK decoder source provenance

These seven C# source files are the decoder dependency closure from Igor
Pavlov's LZMA SDK 26.05 (2026-10-21). They compile directly into OpenBlade's
signed assembly. No third-party executable or DLL is embedded or shipped.

- Official SDK and license: <https://7-zip.org/sdk.html>
- Exact source archive: <https://github.com/ip7z/7zip/releases/download/26.05/lzma2605.7z>
- Archive size: `1786350` bytes.
- Archive SHA-256: `668FDA469B7F584F246008CF28359C66EF98D65FAFB3B22A52672EEBE6970C28`.
- Release tag: `26.05`; associated 7-Zip repository commit
  `b88c6bc09339de32f7d18bbab900686b244f42ba`. The SDK C# subtree is distributed
  in the release archive rather than that repository tree, so the archive hash
  is the source pin.
- License: public domain, as stated in the archive's `DOC/lzma-sdk.txt`.
  `LICENSE.txt` preserves its complete license section. Original documentation
  SHA-256: `987CB5B55EA7D2A2CC1C52E0A3B524A2A8B831ED1E281FCE4E063BDF44185B1F`.

## Local adaptations

The original `SevenZip` namespaces, code comments and implementation are
preserved. The seven source files are flattened into this directory. Each file
has a leading `#nullable disable` for the original pre-nullable code. Namespace
level public types are internal to keep the dependency out of OpenBlade's
public API; types with implicit internal visibility remain unchanged. Newlines and trailing whitespace
are normalized to the repository's CRLF and whitespace conventions. The local `.editorconfig`
marks only these imported C# files as generated code to avoid restyling them.
No algorithm changes were made. Some unused encoder helpers in these shared
files remain to keep the upstream source boundaries intact.

The OpenBlade extraction wrapper owns carrier signature and hash verification,
fixed properties and compressed-span bounds, cancellation, throwing on input
EOF, bounded output, and exact decoded length/hash checks. The upstream
`Code` method does not enforce its input-size argument or cancellation, and
its range decoder casts `ReadByte` results to bytes; it must not receive an
unbounded or EOF-tolerant input stream in this integration.

## Original file hashes

Hashes below cover the exact original SDK archive members, before the listed
local adaptations.

| Original archive member | SHA-256 |
| --- | --- |
| `CS/7zip/Compress/LZ/LzOutWindow.cs` | `6D8252576FDEB885D44CE70CDBA4C56FF4F9A34AD98FE37F13BB9AF3C1E1550C` |
| `CS/7zip/Compress/LZMA/LzmaBase.cs` | `802CF30B9A840413A889691BE4C4D3037FF1963BC2E505C9C40531A3CED2CFD4` |
| `CS/7zip/Compress/LZMA/LzmaDecoder.cs` | `17ADDA814C8AD2E71E079215CFE2F7DD93DE47311B1AFAF07F598A80D41D0F81` |
| `CS/7zip/Compress/RangeCoder/RangeCoder.cs` | `B04F6F722AF6E05DE04801CB95A73AFC73BF7BC22F51F3378418899094C89C26` |
| `CS/7zip/Compress/RangeCoder/RangeCoderBit.cs` | `363389A6D41DA6E625EE0F2FAD264ABA383939F24138238E1DB2385819CA9471` |
| `CS/7zip/Compress/RangeCoder/RangeCoderBitTree.cs` | `32D4A29BBA1A2FC12A56A76D3029A767D1F6B2B9974D9B7451F998538B87BFE2` |
| `CS/7zip/ICoder.cs` | `F796084FA2709EF31EA9979D4B100E0134C7823ED4636D239CFEBF0CB51A504E` |

## Verification

The original seven-file closure compiled independently with no package
references. Its decoder processed all 64 selected file outputs from the five
pinned Razer vendor archives; every decoded size and SHA-256 matched the
independently reviewed recipe metadata. This was static extraction only,
without executing any vendor installer or driver. OpenBlade's wrapper tests
cover malformed input, cancellation and bounds separately.
