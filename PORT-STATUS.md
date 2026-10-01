# Port status: Rusted Шпижион Студия 0.1.5

## Revision 3

Fixed compiler issues found while building the previous C# port:

- `App.axaml.cs`: uses `AvaloniaXamlLoader.Load(this)` for application XAML initialization.
- `IndexDatabase.cs`: fully qualifies `System.IO.Path.GetDirectoryName` because the class exposes a `Path` property.
- `MainWindow.axaml.cs`: fixes the Avalonia 12 `Dispatcher.InvokeAsync(Func<Task<TResult>>)` usage by avoiding a second await of the already-unwrapped result.
- `SettingsWindow.axaml.cs`: adapts `NumericUpDown.Value` to its current non-nullable `double` API.
- `IndexerService.cs`: nullable SQL argument tuple now accepts `object?` and still converts nulls to `DBNull.Value`.
- Native AOT settings now include `IsAotCompatible=true` and `BuiltInComInteropSupport=false`.

The remaining IDE0017/IDE0305/IDE0290/IDE0060/CA1822/SYSLIB1045/CA1861/CA1068 messages are analyzer suggestions rather than compiler errors.

A real `dotnet build/publish` could not be executed in the working environment because the .NET SDK is not installed there.

## 0.1.4-alpha

- Native AOT remains enabled; `PublishSingleFile=false` is used so Avalonia native DLLs remain beside the EXE.
- Release packaging now produces a minimal ZIP bundle instead of publishing a standalone EXE asset.
- Automatic updates verify the ZIP SHA-256 before extraction and retry download verification up to three times with a 5-second delay.
- Bundle replacement is staged externally, preserves portable user data, and rolls back failed file replacement attempts.
- Replaced `DllImport` with source-generated `LibraryImport` and `RegexOptions.Compiled` with `GeneratedRegex` for AOT-friendly interop.
- Fixed partial-scan `Saved=true` reporting after failed SQLite commit and fixed thumbnail disposal.
- SQLite: shared-cache disabled to allow UI reads during long indexing transactions.


## 0.1.2-alpha

- Windows x64 only; cross-platform code paths are no longer required for the release target.
- All application data stays beside the executable.
- No `%AppData%` / `%LocalAppData%` fallback.
- AXAML is explicitly controlled through `AvaloniaXaml` to prevent missing precompiled XAML after migration.
- Portable `config.json` writes use a temporary file and `config.json.bak`.
- Incompatible index versions are detected at application startup and cleared before stale data is exposed to the UI.
- Avalonia 12.1.3 is used for the main packages; `Avalonia.Controls.ItemsRepeater` remains at its available 12.0.0 release.


## 0.1.3-alpha

- Restored parallel parsing/metadata preparation with configurable worker counts.
- UI scan runs off the Avalonia UI thread; cancellation remains on the UI thread.
- Added performance cards, parallelism limits, and dangerous mode.
- Changelog window is borderless.

## 0.1.6-alpha
- Bitmap disposal and background config save implemented.
- Primary/subtype resource filters added.
- Portable index deletion and confirmation dialogs added.

## 0.1.5

- Memory lifecycle fixes for card thumbnails and detail previews.
- Config file save moved off the UI thread to avoid long interface stalls.
- Animated card reveal restored; non-animated mode stays immediate.
- Added multi-select main resource types and subtypes with SQLite classification.
- Added index database deletion action in settings.
- Dangerous mode keeps aggressive in-memory SQLite caching.
- Added borderless confirmations for settings changes, rescans and application exit.
