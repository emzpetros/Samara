# Changelog

All notable changes to this project will be documented in this file.

## [1.0.0] - 2024-01-01

### Added
- Initial release
- Core action framework with ActionStep base class
- Parameter system with string, int, bool, multiline string types
- Variable system with $varname syntax
- Built-in internal variable groups (Application, SystemInfo, Environment, DateTime, etc.)
- Extensible variable groups via VariableGroupRegistry
- JSON persistence (JsonActionRepository)
- SQLite persistence (SqliteActionRepository)
- Reflection-based step discovery
- Built-in action steps for files, folders, settings, packaging
- ActionEditorWindow for creating and editing actions
- ActionRunner for executing action sequences
