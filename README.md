# Diff1cult

A specialized diff tool for analyzing changes in Unity/C# game mods that use Harmony patches. Diff1cult helps mod developers understand how their patched methods have changed between game versions.

## Features

- 🔍 Automatically detects Harmony patches in your mod's source code
- 🎯 Identifies the exact methods being patched
- 📊 Generates an interactive HTML report showing:
  - Side-by-side diff view of changed methods
  - Original (old) source code
  - New source code
  - Your patch code
- 🎨 Smart inline diffing that highlights specific value changes
- 📱 Responsive layout
- 📝 Detailed logging with show/hide functionality

## Usage

```powershell
dotnet run --project Diff1cult.csproj <mod source folder> <old src folder> <new src folder> [--verbose] [--test]
```
or
```powershell
Diff1cult.exe <mod source folder> <old src folder> <new src folder> [--verbose] [--test]
```
### Parameters

- `mod source folder`: Path to your mod's source code containing Harmony patches
- `old src folder`: Path to the old version of the game's source code
- `new src folder`: Path to the new version of the game's source code
- `--verbose`: Enable detailed debug output
- `--test`: Include a test diff example in the output

### Example

```powershell
dotnet run --project Diff1cult.csproj "C:\mods\MyMod\src" "C:\game\v1.0\src" "C:\game\v1.1\src"
```
or
```powershell
Diff1cult.exe "C:\code\MyMod\src" "C:\gameSrc\v1.0\src" "C:\gameSrc\v1.1\src"
```

## Output

The tool generates a `diff_report.html` file containing:
- A summary of found patches and changes
- Interactive list of changed methods
- Side-by-side diff view with:
  - Line numbers for both versions
  - Syntax highlighting
  - Inline change highlighting
  - Resizable panes
- Original and new source code views
- Patch code view
- Detailed execution log (collapsible)

## Features in Detail

### Smart Diffing
- Detects and highlights specific value changes (e.g., `42` → `100`)
- Identifies string literal modifications
- Shows full line changes with proper context
- Maintains code formatting and whitespace

### Interactive UI
- Tab system for different views:
  - Side-by-side diff
  - Original source
  - New source
  - Patch code
- Collapsible debug log

### Harmony Support
- Detects both class-level and method-level Harmony patches
- Supports various patch attribute formats
- Handles `nameof` expressions and string literals
- Maps patch targets to source files

## Requirements

- .NET Core/Framework (compatible with the mod's target framework)
- Source code access to both game versions
- Windows/PowerShell environment

## Contributing

Feel free to submit issues and enhancement requests!

## Credits

Created by:
- [innominata](https://github.com/innominata)
- grok3
- claude3.7

## License

[MIT License](LICENSE) 
