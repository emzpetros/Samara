# Asset Inventory Instructions

## Product Context

- Package: `com.wetzold.asset-inventory`.
- Released commercial editor-only tool.
- Namespace: `AssetInventory`.
- Main assembly: `AssetInventory.Editor`.
- Preserve existing public APIs and serialized data unless the task explicitly asks for a breaking change.

## Fast Context

- `Editor/Scripts/Features/Assets.cs`: asset operations facade.
- `Editor/Scripts/Features/Paths.cs`: path and storage management.
- `Editor/Scripts/Features/Search`: asset and package search.
- `Editor/Scripts/Features/Analysis`: dependency and usage analysis.
- `Editor/Scripts/Persistence`: database adapter and settings.
- `Editor/Scripts/Integrations/MCP`: Asset Inventory MCP tooling.
- `Editor/Scripts/UI`: main window, dialogs, tree views, and graphs.
- `.ai-docs/`: detailed subsystem notes. Read the relevant file before touching a subsystem.

## Package Rules

- This is an editor tool; keep runtime/player dependencies out unless explicitly requested.
- Common is expected to be available through the project/package export model. Do not flag `ImpossibleRobert.Common` usage as missing solely because this source package is viewed in isolation.
- Package-owned assets must be found through package-relative discovery, GUIDs, `AssetDatabase.FindAssets`, package metadata, or paths derived from known package assets.
- Avoid blocking editor UI during indexing, importing, preview generation, or network-related flows.
- When fixing Asset Inventory issues, prefer generalized package/system behavior over targeted workarounds. Do not special-case specific asset packages, publishers, prefabs, custom shaders, or demo content unless the user explicitly requests it.

## Validation

- Tests live in `Assets/Tests/Editor/AssetInventory` using the `AssetInventory.Editor.Tests` asmdef; do not create Asset Inventory tests under `Assets/_Tools/AssetInventory`.
- Use Unity MCP to check console/compile status after code changes.
- For UI, preview, import, drag/drop, or validator work, visually validate the affected window or flow in the existing Unity Editor when practical.
- Before changing database behavior, inspect the affected table models and run or outline a migration/compatibility check.
