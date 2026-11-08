# Independent sanitized wire fixtures

These hand-authored expectations were reviewed against C# at
`dfda7baa4b0ef8210b1b72f24ab321ed113f1da1`; Rust does not generate them.

- `request.json`: the closed GetStatusRequest shape from
  `ServiceMessages.cs:24-25,76,93-94`, Web JSON naming and discriminator.
  After its file newline is removed, the payload is exactly 89 UTF-8 bytes.
  The framing prefix is independently fixed at `59 00 00 00` hexadecimal.
  The C# contract test serializes the real request type against these bytes.
- `response.json`: sanitized projection containing only the fields consumed
  by this prototype plus synthetic private strings and an unknown extension.
  Envelope: `ServiceMessages.cs:577-601`; status: `485-522`;
  telemetry: `Automation.cs:41-46`; version projection:
  `IpcMessageCodec.cs:113-145`. Unused required C# snapshot fields are omitted
  deliberately: this is a whitelist-reader fixture, not a complete snapshot for
  round-tripping through the C# closed snapshot reader.
- `summary.json`: independently written output whitelist, preserving signed
  flow, i64 revision and nulls. None of the synthetic private strings belongs
  in output.

The GUID and every private-looking value are synthetic. No live service status,
user path, application profile or serial is included.
