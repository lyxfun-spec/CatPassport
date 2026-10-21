<p align="right"><strong>English</strong> · <a href="context-management.zh_CN.md">简体中文</a></p>

# Context management

The Windows backend preserves messages, runs, tool calls, approvals, and artifacts as authoritative SQLite records. Model context is a derived view: an active validated summary, bounded recent original events, the current input, system instructions, tool schemas, loaded skill/tool results, and an output reserve.

Before every Chat turn and Work model round, the backend serializes the actual provider request shape and applies a model profile. The default profiles reserve 4,096 output tokens plus a 5% safety margin. Compaction is scheduled at 70% of the input budget and prioritized at 80%, targeting a substantially smaller request. Provider usage is stored when available; otherwise the diagnostic explicitly records the conservative UTF-8 estimate.

Compaction is a separate low-temperature request using the current conversation model and API settings. It runs only at a safe boundary. Work never compacts an incomplete assistant tool-call/result group. Summaries are versioned with their covered range and source summary, validated for a minimum size reduction, then activated transactionally. Failed compaction leaves the previous summary and all original history intact. Oversized compression input is processed in bounded batches.

Search uses SQLite FTS5 plus a dependency-free local Unicode word/character n-gram vector, merged with reciprocal-rank fusion. Vectors are derived data keyed by stable message ID and content hash. If FTS5 or vector indexing fails, bounded keyword search remains available. The local vector improves fuzzy wording and mixed Chinese/Latin recall but is not a neural embedding model; the `ILocalEmbeddingProvider` boundary permits a packaged ONNX model without changing storage or ranking semantics.

Chat exposes only `search_conversation_history`, scoped to the current conversation. It supports bounded search and stable-message-ID lookup. Work receives no new model tool. Instead, the application writes `work/.context/history.jsonl`, `status.json`, and `search_history.py` inside the existing conversation workspace. This copy is one-way, redacts credential-shaped tool fields, reports a watermark and integrity hash, and can be deleted or modified without affecting authoritative history.

Large Work tool results are retained in the authoritative tool record and written completely under `work/.context/tool-results/`. The model receives a short preview and workspace-relative path. Approval state, active runs, pending user messages, and tool terminal states never rely on a model summary.

Known limits:

- Model context windows are internal profiles and must be updated when a provider changes published capabilities.
- The fallback estimator is intentionally conservative and is calibrated only when the provider returns usage.
- A single current input that cannot be split may still exceed the smallest safe request; the backend preserves progress and reports that condition instead of truncating it.
- Work history copies refresh at model-round and completion boundaries, not continuously while a tool is still running.
