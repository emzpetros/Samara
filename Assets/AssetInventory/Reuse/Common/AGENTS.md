# Common Instructions

## Scope

- Package: `com.wetzold.common`.
- Shared utilities for the commercial tools in this workspace.
- Treat changes here as cross-package changes. Inspect at least one existing usage in affected tools before changing behavior or public APIs.

## Rules

- Keep shared code tool-agnostic. Do not add dependencies from Common to individual commercial tools.
- Editor-only helpers belong in the Common editor assembly. Runtime utilities must not depend on UnityEditor.
- Preserve public APIs used by released tools unless the task explicitly asks for a breaking change.
- Prefer small, explicit utilities over broad abstractions.

## Validation

- After Common changes, check affected asmdef references and compile impact across tools that use `ImpossibleRobert.Common`.
- Use Unity MCP to inspect compile errors and console logs in the existing editor instance when available.
