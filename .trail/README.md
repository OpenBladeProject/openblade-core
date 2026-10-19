# Trail-Code for OpenBlade

`cache/` contains the local, generated code index and is ignored by Git.
`learnings/` holds optional project notes that can be shared with the code.

Codex uses the installed Trail MCP server and lifecycle hooks. Retrieve code
with `trail ask "<question>" --source`, search with `trail grep "<symbol>"`,
or inspect dependencies with `trail callers <symbol>`.

`trail build` refreshes the deterministic index without a model or API key.
LLM summaries are optional and require a separate `trail build --deep`.
