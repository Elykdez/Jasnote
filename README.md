# Jesnote

<p align="center">
  <a href="https://github.com/Elykdez/Jesnote"><img src="src\Resources\Icons\logo.png" alt="Jesnote" width="96" /></a>
</p>
<p align="center">
  <p align="center"><strong>A desktop app for inspecting and editing super large JSON | JSONL documents.</strong></p>
</p>
<p align="center">
  English | <a href="./README_CN.md">中文</a>
</p>
<p align="center">
  <a href="https://github.com/Elykdez/Jesnote/actions/workflows/ci-cd.yml"><img alt="CI/CD" src="https://github.com/Elykdez/Jesnote/actions/workflows/ci-cd.yml/badge.svg" /></a>
  <img alt="Avalonia" src="https://img.shields.io/badge/UI-Avalonia-0078D4" />
  <img alt="JSON" src="https://img.shields.io/badge/data-JSON-2F7D32" />
  <img alt="Windows and Apple Silicon macOS" src="https://img.shields.io/badge/platform-Windows%20%7C%20macOS%20ARM64-0078D6" />
  <a href="https://github.com/Elykdez/Jesnote/releases"><img alt="Release" src="https://img.shields.io/github/v/release/Elykdez/Jesnote?label=release" /></a>
</p>

- Built with a virtualized tree view so large files are browsed without creating a UI node for every JSON element.

## Features

- Browse JSON data as an expandable tree.
- Open files from the file picker, clipboard, drag and drop, or a command line argument.
- View very large JSON files, like >10GB documents with 100M+ elements.
- Loading can be canceled at any time without heavy memory issues or UI freezes.
- Search keys and values with wildcard patterns.
- Edit nodes or union data with full undo and redo support.
- Export the selected JSON branch to a new file or copy it to the clipboard.
- Switch between light and dark themes.
- Locale support: English, Chinese, French, Japanese, Korean, Portuguese, Russian, Spanish.

## Requirements

- Windows 10 or macOS (Apple Silicon) with .NET Runtime 8 or newer.

## Performance

[![Demo](demo.png)](./)

- Instant opening and rendering.
- Fully loaded and rendered a 13GB JSONL with 66M elements in approx. 53 sec. (on a HDD).
- Such JSONL file in Classic mode consumes approximately 7GB of additional memory, whereas Compact Mode consumes only about 500MB.
- This result is a reference measurement, actual speed can vary by device hardware, especially CPU, memory capacity, storage speed, and thermal conditions.

## Search patterns

Choose the search type first, then enter a pattern:

- **Key**: searches JSON property names.
- **String**: searches JSON string values.
- **Number**: searches numeric values.
- **Keyword**: searches only `true`, `false`, or `null`.

Pattern behavior:

- In **String** search, plain text is treated as **contains**.
  - `user` matches `user`, `username`, and `current_user_id`.
- `*` is the wildcard character.
  - `user*` = starts with `user`
  - `*user` = ends with `user`
  - `*user*` = contains `user`
- In **Key**, **Number**, and **Keyword** search, patterns keep the original wildcard behavior.

Search starts from the current selection and moves forward. If nothing is found before the end of the document, Jesnote asks whether to continue searching from the top.

## Getting started

Download the latest packaged build from the [releases page](https://github.com/Elykdez/Jesnote/releases), extract it, then run the executable for your platform.

If you want to build or run from source, see [CONTRIBUTION.md](./CONTRIBUTION.md).

## Plan

- Improve UI
- Add more Json editing tools

## Attributions

- GPT 5.5 for code assistance and documentation.
