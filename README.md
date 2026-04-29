# MediaSort

A keyboard-friendly Windows desktop app for quickly sorting images and videos into destination folders. Built with WPF on .NET 9.

![Built with .NET 9](https://img.shields.io/badge/.NET-9.0-blueviolet) ![Platform: Windows](https://img.shields.io/badge/platform-Windows-blue)

## Features

- **Three-pane layout**
  - **Left** — Source folder list with three view modes: **List**, **Details**, and **Thumbnails**. Traverse with the arrow keys, Home/End, or the position slider.
  - **Center** — Live preview pane. Images render as full bitmaps; videos play with Play / Pause / Stop controls.
  - **Right** — Destination buttons. Each one binds to a folder and an optional keyboard shortcut.
- **Move on keypress** — Press a destination's hotkey (or click the button) to move the currently selected file to that folder. The list advances automatically.
- **Wide format support**
  - Images: `.jpg .jpeg .png .gif .bmp .webp .tif .tiff .heic .heif .ico .jfif`
  - Videos: `.mp4 .mov .avi .mkv .wmv .webm .flv .m4v .mpg .mpeg .3gp .ts .mts`
- **Sortable columns** in Details view (name, size, modified date).
- **Recursive scanning** option.
- **Persistent settings** — source folder, view mode, and destination buttons are saved to `%APPDATA%\MediaSort\settings.json`.
- **Settings & About** dialogs in the top toolbar.
- **Auto-versioning** — every push to `main` bumps the patch version automatically via GitHub Actions, and the running version is shown in the title bar and About dialog.

## Building

### Requirements
- Windows 10 / 11
- Visual Studio 2022 or 2026, with the **.NET desktop development** workload, OR
- .NET 9 SDK on the command line

### Visual Studio
1. Open `MediaSort.sln`.
2. Build > Build Solution (Ctrl+Shift+B).
3. F5 to run.

### Command line
```powershell
dotnet restore MediaSort.sln
dotnet build MediaSort.sln -c Release
dotnet run --project MediaSort/MediaSort.csproj
```

## Usage

1. Click **Pick Source Folder...** in the top toolbar and choose a folder containing media.
2. Choose a view mode (List / Details / Thumbnails). Optionally enable **Recursive** to include subfolders.
3. Click **+ Add** in the right panel to create a destination button. In the editor:
   - Set a **Name**.
   - Pick a **Folder**.
   - Click in the **Hotkey** field and press a key (with optional Ctrl/Alt/Shift). Backspace clears the binding.
4. Select an item in the source list (arrow keys, slider, or click). The preview appears in the center.
5. Press the destination's hotkey (or click the button) to move the file. The list automatically advances to the next item.

## Versioning

The version number is stored in `MediaSort/MediaSort.csproj` (`<Version>`, `<FileVersion>`, `<AssemblyVersion>`). The GitHub Actions workflow at `.github/workflows/version-bump.yml` increments the patch component on every push to `main` and commits the change back. To skip a bump on any commit, include `[skip version]` in the commit message.

## License / Copyright

Copyright © Delos Technologies
