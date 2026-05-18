# Contributing to Jesnote

Jesnote is a cross-platform Avalonia desktop JSON/JSONL viewer with one job: open files measured in gigabytes, with element counts in the tens of millions, and let the user navigate without OOM or UI freezes.
Every architectural decision flows from that constraint. Before changing anything that touches parsing, the tree document, or rendering, read this file.

## Build and run

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or newer. From the repo root:

```powershell
.\run.bat                  # build + run (no file)
.\run.bat .\sample.json    # build + run with file argument
dotnet run --project .\src\Jesnote.csproj -- .\sample.json
dotnet restore .\src\Jesnote.csproj
dotnet build .\src\Jesnote.csproj -c Release
```

The Release build output is written to:

```text
src\bin\Release\net8.0\
```

You can also build the solution file if your SDK supports `.slnx`:

```powershell
dotnet build .\Jesnote.slnx -c Release
```

The csproj enables `ServerGarbageCollection`, `ConcurrentGarbageCollection`, `TieredCompilation`, and `TieredPGO`. Do not turn these off without measuring.

## Release

Use `release.bat` from the repository root to prepare a local release commit and tag:

```powershell
.\release.bat 0.1.0
```

The script updates the `<Version>` tag in [src/Jesnote.csproj](src/Jesnote.csproj), builds the Release configuration, commits the current working-tree changes, and creates tag locally. It does not push anything automatically.

**Always bump `<Version>` in [src/Jesnote.csproj](src/Jesnote.csproj) for every release.** `release.bat` does this for you, but if you publish out-of-band (manual tag, hotfix, custom workflow), update that one line by hand before tagging - the assembly version, the About dialog, and the GitHub release name all read from it. A tag without a matching `<Version>` bump will ship a binary that still reports the previous number.

Review `git status` before running `release.bat` because the script commits all current changes. When the local release looks correct, push the branch and tag manually:

```powershell
git push origin main v0.1.0
```

GitHub Actions will build and publish Windows x64 and Apple Silicon macOS release assets after the tag is pushed. The workflow publishes framework-dependent packages (`--self-contained false`), so end users need the .NET 8 Runtime installed.

Do not delete a published GitHub release just to republish the same tag. GitHub immutable releases can permanently block reusing a deleted tag name. Prefer a new patch version if the published release has already become immutable.

## Project layout

Put portable parser/settings/localization code in [src/Scripts](src/Scripts), Avalonia UI code directly under [src](src), and assets in [src/Resources](src/Resources).

## Performance budget

The README target: open documents above 2 GiB with 100M+ elements without heavy memory issues or UI freezes. Concretely:

Per-element heap cost stays at or under ~30 bytes plus a per-string-value entry. No intermediate fully-materialized JSON tree (no `JsonDocument`, no `Dictionary<string, object>`, no boxed value tree) is built; parsing writes directly to the compact representation. No per-element UI tree node exists; the tree is custom-rendered and virtualized. Files larger than 1 GiB never get loaded as a single managed `byte[]`; they use the streaming/chunked path.

A full boxed object graph is usually several times larger than the input file and becomes the dominant memory cost on huge documents. Jesnote skips that step entirely. If you find yourself adding code that materializes the whole input as managed objects before scanning, stop and reconsider.

## Tree memory layout

Parallel arrays indexed by node id, defined in [JsonTreeDocument.cs](src/Scripts/Core/JsonTreeDocument.cs):

```text
byte[]    Types[i]        node type
string?[] Keys[i]         interned object key, or "[i]" for array elements
long[]    Values[i]       discriminated slot: double bits / encoded string ref / 0|1 / unused
int[]     Parents[i]      parent id, or -1 for root
int[]     FirstChild[i]   first child id, or -1
int[]     NextSibling[i]  next sibling id, or -1
```

Total: 29 bytes/node, plus whatever the active `StringStorage` chooses to use for the actual String values. A hypothetical `class JsonNode` design (object header 24 bytes + fields + per-parent `List<int>` headers + boxed numbers) would be 80+ bytes/node and OOM well below the target.

Object keys use a per-parse `Dictionary<string, string>` interner. Typical JSON has fewer than 200 distinct property names regardless of document size; this collapses millions of duplicate key strings to one each. The dictionary lives in the `BuildContext` local and is GC'd as soon as parse finishes.

Children form a linked list (`FirstChild` + `NextSibling`) instead of a per-parent `List<int>`. Lists cost ~40 bytes of overhead each, and the document can have millions of parents. The linked-list cost is paid only in CPU when iterating children, which is rare on the hot path.

The `long Values[]` slot is a discriminated union driven by `Types[i]`. Numbers are stored as `BitConverter.DoubleToInt64Bits`. Booleans are 0/1. For String nodes, the slot holds an encoded ref returned by the active `StringStorage`; only that storage knows what the bits mean. The boxed `ValueOf(id)` accessor exists for compatibility but should not be used on hot paths.

Array index keys (`[0]`, `[1]`, ...) are deduped via a static cache of 4096 entries so log-style documents do not allocate one short string per index.

When you add a node type or change the value layout, verify the per-node bytes are still bounded and update the comment block at the top of [JsonTreeDocument.cs](src/Scripts/Core/JsonTreeDocument.cs).

## String value storage

String values are routed through a [StringStorage](src/Scripts/Core/StringStorage.cs) abstraction. The document holds one instance, picked by `StringStorageMode` at every `Reset()`/load start, and every String-related hot path (parse append, read, capped read, regex match, JSON-write) delegates to it. The `long` returned by `Append` is what `Values[id]` holds; the storage owns the interpretation of those bits.

Two implementations ship today:

`Utf8ChunkStringStorage` (default, "Compact"). Raw UTF-8 bytes append into 16 MiB `byte[]` chunks. The encoded ref packs `(chunk:16, offset:24, length:24)` into one `long`, so per-node memory is unchanged. The empty string is sentinel `0L`, allocates nothing. No CLR `string` is created at parse time - the build path calls `Utf8JsonReader.CopyString` straight into a chunk. Reads decode on demand with `Encoding.UTF8.GetString`. Search decodes into a pooled `char[]` before `Regex.IsMatch(ReadOnlySpan<char>)`. Extract writes raw UTF-8 spans through `Utf8JsonWriter.WriteString(name, ReadOnlySpan<byte>)`, skipping the UTF-16 round-trip. On a 13 GiB CJK/long-text JSONL this is roughly 40% lower managed-heap than `PooledStringStorage`. The trade-off is allocating a fresh `string` on every read and decoding per candidate during full-document string search.

`PooledStringStorage` ("Classic"). One CLR `string` per value in a parallel array, grown by doubling. The encoded ref is just the int index. Higher memory but every read is a free reference return, and regex search runs directly against the existing string instances.

If you add a new storage strategy: register it in `StringStorage.Create`, add the enum value to `StringStorageMode`, add a localized name to all `Strings*.resx` files, and wire it into the settings dialog.

## Concurrent reads during streaming load

The JSONL streaming path (`StreamJsonlAsync`) attaches the document to the tree *before* parsing starts and grows it live; the UI thread reads while the parser thread writes. To keep that safe without locking the hot path:

`int _count` is the parser-private node counter, incremented every `Add*` call.

`int _publishedCount` is what `Count` returns via `Volatile.Read`. The parser bumps it via `Publish()` (Volatile.Write + `DocumentGrew` event) every ~33 ms and once at the very end. Readers only access ids in `[0, Count)`.

Children walks (`ChildCount`, `ChildIds`, and the corresponding loops in [VirtualJsonTree.cs](src/VirtualJsonTree.cs)) gate sibling traversal on `c < published`. The writer eagerly links new top-level lines into the synthetic root via `Frame.IsStreamingRoot` + `Frame.Tail` (it never accumulates them in a per-frame `List<int>`), but the reader still filters in case it walks into an id that hasn't been published yet.

The grow-on-write node arrays (`Types`, `Keys`, `Values`, `Parents`, `FirstChild`, `NextSibling`) and the `StringStorage` backing arrays both follow a "publish reference, then write index" rule: when a chunk or capacity-doubled array is allocated, the new reference is published via `Volatile.Write` *before* any `Values[id]` that points into it becomes observable. Readers snapshot via `Volatile.Read` so they always see a self-consistent (array, count) pair.

Don't add a new background-thread writer without following this pattern, and don't read the parser-private `_count` from the UI side - always go through `Count`.

## Parse pipeline

Parse paths share the same `HandleToken` core and the same compact-array output. JSONL takes a different shape from regular JSON because each line is independent:

```text
LoadAsync(path), non-jsonl  size <= 1 GiB    in-memory two-pass:  CountTokens + BuildFromSpan
                            size >  1 GiB    streaming two-pass:  CountFromStreamAsync + BuildFromStreamAsync
LoadAsync(byte[]),non-jsonl always in-memory two-pass
LoadAsync(path, jsonl)      any size         streaming single-pass: StreamJsonlAsync
```

Non-JSONL paths still run two passes. The count pass walks tokens and accumulates `TotalCount` and `StringCount` with no allocations beyond the reader state. The build pass calls `AllocateArrays(TotalCount, StringCount)` exactly once, then emits nodes into the pre-sized arrays.

Pre-sizing matters for the two-pass paths: `List` growth doubles capacity, which on a 100M-node array means a 400 MB to 800 MB resize copy at the worst moment. Two passes pay a 2x I/O cost (the OS page cache absorbs the second read for files smaller than RAM) but eliminate reallocation during the build.

JSONL skips the count pass entirely - it cannot afford the second IO pass on a 13 GiB file when the goal is to show first rows in under a second. Instead it streams once, appends nodes into grow-on-write arrays (`EnsureNodeCapacity` doubles capacity on demand), and publishes batches of completed lines to the UI via the `_publishedCount` mechanism documented in the "Concurrent reads during streaming load" section above. The amortized cost of `Array.Resize` doublings is fine; the one-time GC of the superseded arrays is reclaimed by the post-load `ReleaseParserGarbage()` LOH compaction in [MainWindow.cs](src/MainWindow.cs).

The streaming paths use `ArrayPool<byte>.Shared.Rent(InitialBufferSize)` (256 KiB initial) with chunked reads. If a single JSON token (typically a huge string literal) exceeds the buffer, the buffer doubles up to `MaxStreamChunkBuffer` (1 GiB). Beyond that, the parser throws an `InvalidDataException` with a clear message instead of OOM.

## JSONL

JSON Lines (`.jsonl`, `.ndjson`) puts one complete JSON document per line. .NET 8's `JsonReaderOptions` does not have `AllowMultipleValues` (that's a .NET 9 API), so the JSONL paths split on `\n` and parse each line independently with `Utf8JsonReader(line, isFinalBlock: true, default)`.

Each top-level value becomes a child of a synthetic Array root added during `StreamJsonlAsync` startup (`AddArray(string.Empty)` at id 0, with a `Frame.IsStreamingRoot = true` pushed onto a context that lives for the whole load). The synthetic root is hidden by the tree control, so the user sees the records as top-level siblings keyed `[0]`, `[1]`, ...

Crucially, top-level values are linked into the root chain eagerly via a `Frame.Tail` pointer (`FirstChild[root] = id` on the first line, `NextSibling[oldTail] = id; tail = id` thereafter). This avoids accumulating a multi-million-entry `Children: List<int>` that would itself blow the per-load memory budget. UI readers still filter sibling walks by `Count` because the writer may have linked an id that isn't published yet.

Detection is by file extension only ([MainWindow.IsJsonlPath](src/MainWindow.cs)). The picker filter is `*.json;*.jsonl;*.ndjson`. Trailing CR (CRLF) is stripped. Leading and trailing ASCII whitespace is stripped. Empty lines are skipped. A malformed line throws `JsonException` with the byte offset; the main window displays it in the status footer.

Cancellation mid-stream is non-destructive: when the user cancels and the document already has top-level children, [MainWindow.LoadCoreAsync](src/MainWindow.cs) keeps whatever was published and surfaces "{N:N0} elements available" in the status. The synthetic-root-only case (cancelled before any line completed) is treated as a normal cancel and resets the doc.

## UI virtualization

[VirtualJsonTree](src/VirtualJsonTree.cs) is a custom-rendered Avalonia `Control`, not `TreeView` or `ListBox`. Per-node UI controls are fatal at millions of elements.

State:

`_visible: List<int>` is the flattened list of currently visible row ids (only expanded branches contributed).
`_depths: List<int>` is the matching depth per visible row.
`_openBranches: HashSet<int>` tracks which branches are open.

Opening or closing a branch splices descendants in or out of `_visible` and `_depths`. That is O(visible delta), not a full re-walk of the document. A 100M-element doc with everything collapsed has a `_visible` of size 1.

Rendering draws only `Bounds.Height / rowHeight` rows. Avoid per-row control creation and keep per-render allocation low.

`Expand All` is disabled for documents with more than 1000 elements. There is no realistic way to display every node, and `List.InsertRange` on a 100M-entry list would be catastrophic.

Per-row string display is capped at 200 characters. The detail pane caps display at 64 KiB for responsiveness; extraction and clipboard export still use the full stored value/subtree.

## Progress reporting

`JsonTreeDocument` reports progress via `IProgress<ProgressInfo>`. The Avalonia window creates the progress object on the UI thread and displays load status in the footer.

Two gotchas bit us; do not regress them.

Throttling still matters: a 100M-element parse can fire ~10 000 progress reports during the build pass. If a modal loading UI returns, do not dispatch every report directly to the UI thread. Step transitions and the final `Progress >= 1.0` should always be displayed.

Bucket-diff vs modulo: the regular JSON path checks `Count % ProgressTick == 0` after every token, where `Count` increments by exactly 1, so every multiple is always seen. The JSONL path checks after every line, where `Count` jumps by however many tokens that line contained. If lines have a uniform shape (an object with 16 fields gives 17 tokens), `Count` never lands on a multiple of `ProgressTick` and the modulo check never fires; no progress, no cancellation, frozen dialog until the final `Progress=1.0`. Use the bucket-diff helper `ReportJsonlProgress`, which compares `Count - _lastProgressCount >= ProgressTick`. If you add a new parse path that updates `Count` in batches, use bucket-diff, not modulo.

## Frame pool

The build pass uses a per-parent `Frame { Children: List<int> }` to collect child ids before linking and (for objects) alphabetical sorting. Frames are pooled and reused via `RentFrame` / `ReturnFrame` in [JsonTreeDocument.cs](src/Scripts/Core/JsonTreeDocument.cs).

`ReturnFrame` previously called `f.Children.Clear()`, which keeps the backing `int[]` capacity. After parsing a single wide array of N children, the frame's `Children` was sized to N; on the next parse, that capacity stayed pooled. For documents with a 50M-child array, that meant hundreds of MB retained across loads. The current code replaces `Children` with a fresh small `List<int>(4)` when capacity exceeds 1024. If you change the frame pool, do not regress this.

## Cancellation

Inside parse loops, check `ct.ThrowIfCancellationRequested()` at least every few thousand tokens or every chunk read. Do not put it inside the progress-fires gate; that is the bug we hit when modulo-progress never fired and cancellation rode along on it.

If parse throws `OperationCanceledException`, `_doc` may be left in a partial state, but `_tree.Document` should not be assigned until load success. The next load calls `AllocateArrays`, which overwrites everything cleanly.

## Search and extract

Search uses [Wildcard.Compile](src/Scripts/Core/Wildcard.cs) (depth-first, ignores the start node, returns the next match). Search runs on a `Task.Run` so the UI stays responsive; the search loop checks `ct.ThrowIfCancellationRequested` between nodes.

Extract walks a subtree via `Utf8JsonWriter` straight into a `MemoryStream`. No intermediate boxed objects. Only Array and Object can be extracted.

## Avalonia UI

Jesnote uses Avalonia's classic desktop lifetime (`StartWithClassicDesktopLifetime`) and builds the UI in C# rather than XAML. The application root is [App.cs](src/App.cs), the main top-level window is [MainWindow.cs](src/MainWindow.cs), and the custom tree control is [VirtualJsonTree.cs](src/VirtualJsonTree.cs).

Keep UI code in Avalonia terms:

- Use `MenuItem.HotKey = new KeyGesture(...)` for menu shortcuts instead of manual `KeyDown` dispatch when the command already belongs to a menu item.
- Use Avalonia storage APIs (`StorageProvider.OpenFilePickerAsync`, `SaveFilePickerAsync`) rather than WinForms or platform-specific dialogs.
- Use `ShowDialog(owner)` for modal child windows. If the child is created in code, set the title/icon/theme before showing it and refresh any open dialog content when the user changes language or theme from inside that dialog.
- Keep compact toolbar controls icon-like. Use text labels only where they carry domain meaning; for repeated navigation controls, prefer symbolic content plus localized `ToolTip` text.
- Do not introduce WinForms/WPF concepts such as `DockStyle`, `ShortcutKeys`, or per-node tree controls into new Avalonia code.

Avalonia layout can resize aggressively. Avoid depending on default button width for icon buttons; give fixed-format controls explicit `Width`, `MinWidth`, or layout constraints so localized text cannot stretch unrelated toolbar actions.

### Sample

```xml
<Grid ColumnDefinitions="150,Auto,*,2*">
    <!-- 
      Column 0: Exactly 150 pixels wide
      Column 1: Sizes automatically to fit its content
      Column 2: Gets 1 part of the remaining space (33.3%)
      Column 3: Gets 2 parts of the remaining space (66.6%)
    -->

    <!-- To place a control in a column, use the Grid.Column attached property -->
    <Button Grid.Column="0" Content="Fixed Width" />
    <TextBlock Grid.Column="1" Text="Fits perfectly" />
    <TextBox Grid.Column="2" Text="Fills space" />
    <TextBox Grid.Column="3" Text="Fills double space" />
</Grid>
```

- This project uses code instead of XML for UI construction which is intentional to keep the codebase clean, but the layout technique works the same.

## Theming

Avalonia handles light/dark styling through `RequestedThemeVariant` and the Fluent theme. The custom tree chooses its palette from the current Avalonia theme variant.

On Windows, the title bar is drawn by DWM, outside Avalonia's normal render tree. [WindowChrome.cs](src/Scripts/WindowChrome.cs) applies `DwmSetWindowAttribute` after a native window handle exists. Keep this helper simple: set the desired dark/light state when the window opens and call it explicitly after a user theme change. Avoid focus/activation-based chrome updates; they can cause flicker.

If you add a new custom-rendered control, verify it reacts correctly to light and dark variants.

## Localization

UI strings live in `.resx` files under [src/Resources](src/Resources). `Strings.resx` is the neutral English resource, and culture-specific files such as `Strings.zh-CN.resx`, `Strings.fr.resx`, and `Strings.ja.resx` must keep the same key set unless there is an intentional fallback.

Language names in the settings dropdown are not localized through `.resx`. [Localization.LanguageName](src/Scripts/Localization.cs) returns each language's native display name, such as `English`, `简体中文`, so every locale sees the same stable names.

When adding a locale:

1. Add the enum value to `LanguagePreference`.
2. Map it in `ResolveCulture` and `ResolveAutoCulture`.
3. Add it to the language preference list in `MainWindow`.
4. Add a full `Strings.<culture>.resx` file with the same UI keys as `Strings.resx`, excluding `Language.*`.
5. Verify key parity across resources and build.

Changing language while a dialog is open should update that dialog too. For settings-style dialogs, keep references to the visible label controls and repopulate localized choice lists after `Localization.Apply(...)`.

## Settings

[AppSettings](src/Scripts/AppSettings.cs) is JSON-serialized to the current platform's application-data folder under `Jesnote/settings.json`. Load and save failures are swallowed; they are best-effort. To add a field, add the property with a sensible default; `PropertyNamingPolicy = CamelCase` is already set; update the settings dialog in [MainWindow.cs](src/MainWindow.cs) if it should be user-facing.

`StringStorage` is a per-load setting: `MainWindow.LoadCoreAsync` reads it into `_doc.StringStorageMode` immediately before `_doc.Reset()`, so toggling the option in the dialog takes effect at the next file open. Display labels for the dropdown live in `StringStorage.<mode>` resource keys and should describe the trade-off inline (e.g. "Compact (low memory, slower search)") so users do not need a separate help blurb.

## Things deliberately not done

Linux packaging: the app now uses Avalonia and the project is portable, but CI currently publishes Windows x64 and Apple Silicon macOS packages only.

Memory-mapped file I/O: [PLAN.md](PLAN.md) mentions it; the current code uses `ReadAllBytesAsync` for files <=1 GiB and chunked streaming above. The OS page cache makes the second pass effectively free for files that fit in RAM. mmap would only save 1 GiB of managed-heap pressure during the build pass and requires unsafe spans. Reconsider if profiling shows it is the bottleneck.

Find-all-matches UI: the current search workflow returns one match at a time and the user re-searches.

Streaming JSON (not JSONL) tokens larger than 1 GiB: a single token bigger than `MaxStreamChunkBuffer` errors out cleanly. Files where this happens are pathological.

Per-row text width caching during render: text measurement runs for visible rows on each render. Keep it out of parser paths and revisit only if profiling shows scroll jank.

## Adding a feature

Match the existing style: Allman braces, file-scoped namespaces, ASCII-only in source files, no emojis or em dashes.

Read the surrounding code carefully. The hot paths in `JsonTreeDocument` and `VirtualJsonTree` have deliberate constraints (no per-node boxing, no per-render allocation). Adding a `List<Foo>` in the parser path is almost always wrong; ask whether the data really needs to live anywhere other than the existing parallel arrays.

Verify the build passes:

```powershell
dotnet build .\src\Jesnote.csproj -c Release
```

Smoke-test against [sample.json](sample.json) and at least one large file (1 GiB or more) before claiming a change is done. Memory and timing regressions on big inputs do not show up on small inputs.

When changing parse code, watch for: allocations inside the token loop (anything you allocate runs O(N) where N can be 100M); memory not freed at parse end (`BuildContext` and any pooled state should reset cleanly); progress reports in batched-`Count` paths must use bucket-diff; cancellation must check at least every chunk or every few thousand tokens; the compact-array sizing must include any synthetic nodes (such as the JSONL root).

When changing UI code, watch for: per-render allocations (brushes, pens, strings - cache them); `_visible.IndexOf(id)` is O(visible) and acceptable only for navigation events (do not call it inside render); operations on `JsonTreeDocument` from a background thread (the parallel arrays are not synchronized; reading them from render while the build is mutating them will produce garbage, currently avoided by assigning the document only after load success).

## Performance: memory-saving techniques

This is the long-form rationale for the architectural decisions above. If you change parse code, this section is the test you have to pass: any new design must improve on or at least match the current memory budget.

### Boxed-tree pipeline to avoid

A common loader shape uses three observable steps and is easy to reason about, but it is too memory-heavy for Jesnote's target:

Step 1, load: a decoder reads the full file and produces a recursive boxed object graph: dictionaries for objects, lists for arrays, boxed numbers and booleans, strings, and null markers.

Step 2, size: a depth-first walk of that graph counts nodes. This adds little allocation, but the expensive graph already exists.

Step 3, render: another walk copies the graph into compact indexed storage. Object keys are sorted alphabetically per parent before adding. Progress and cancellation are checked on a fixed cadence.

### Where boxed trees spend memory

The dominant cost is the intermediate boxed tree built in step 1. For a multi-gigabyte JSON file with tens of millions of elements, that tree can be several times larger than the source data because:

Every dictionary entry pays hash-table overhead plus the key string and a boxed value reference. Numbers and booleans become boxed primitives. Strings carry both an object header and payload. Arrays add list headers plus backing arrays.

Even if the compact representation replaces that graph after render, peak memory is still the sum of the boxed tree plus the compact representation until the GC collects the old data.

Steady-state memory in a compact indexed form, per element:

`Node[]`: a key reference, boxed value reference, node type, and padding.
`int[] parents`: 4 bytes per node.
Parent-to-children map: hash-table overhead plus a child-list header per parent. Cheap for small documents, expensive when there are millions of parents.

So a compact indexed form can be reasonable at steady state, but the transient boxed-tree cost during load is still too high.

### How Jesnote adapts each step

Step 1 (avoid building a boxed tree). Jesnote runs `Utf8JsonReader` directly over the bytes. There is no intermediate tree at all. `Utf8JsonReader` is a `ref struct` that yields tokens without allocating per token. The only allocations during the count pass are short-lived: the reader state, a 256 KiB chunk buffer for the streaming path, and nothing else. This is the largest memory win in the parser.

Step 2 (count without a separate tree walk). Jesnote folds this into the first pass: `CountTokens` and `CountFromStreamAsync` accumulate `TotalCount` and `StringCount` while they read tokens. Same number of token reads, no separate tree to walk.

Step 3 (emit compact nodes). Jesnote's `BuildFromSpan` and `BuildFromStreamAsync` run a second token pass and emit nodes into pre-sized arrays. The `BuildContext` uses a `Stack<Frame>` to track open object/array frames; on `EndObject` the frame's collected children are sorted alphabetically by key via `span.Sort(new KeyComparer(Keys))` (struct comparer, no closure allocation) and then linked via `FirstChild` and `NextSibling`.

Per-element steady-state in Jesnote:

`byte[] Types`: 1 byte.
`string?[] Keys`: 8 bytes for the reference; key strings themselves are interned per parse.
`long[] Values`: 8 bytes discriminated slot. For String nodes, the slot holds the encoded ref returned by the active `StringStorage` (a chunk/offset/length tuple in Compact mode, an `int` pool index in Classic mode) - in both cases zero extra bytes per node beyond the existing `long`.
`int[] Parents`, `int[] FirstChild`, `int[] NextSibling`: 12 bytes total.

Total: 29 bytes per node plus the actual string content, stored by the chosen `StringStorage` strategy. The String content cost is the file's UTF-8 bytes (Compact mode) or one CLR `string` per value (Classic mode); see the "String value storage" section above. Compared with a compact-but-boxed indexed form, this removes boxed primitive values and per-parent child-list overhead on top of eliminating the transient object graph.

### Streaming and very large files

A full-tree loader does not stream. The full file must fit in RAM after expansion into managed objects, so failure mode is usually OOM.

Jesnote draws a line at 1 GiB (`InMemoryLoadLimit` in [JsonTreeDocument.cs](src/Scripts/Core/JsonTreeDocument.cs)) for **non-JSONL** input. At or below 1 GiB the file is `ReadAllBytesAsync`'d into a managed `byte[]` and parsed twice from that span; the OS page cache makes the second pass effectively free. Above 1 GiB the streaming path opens the file twice (`FileOptions.Asynchronous | FileOptions.SequentialScan`, 1 MiB buffer), reads in chunks, and feeds `Utf8JsonReader` with `isFinalBlock: false` plus persisted `JsonReaderState` across chunks. The buffer doubles up to 1 GiB if a single token (a huge string literal) does not fit; beyond that, the parser throws `InvalidDataException` instead of OOM.

JSONL always streams - the file is read once, lines are parsed in isolation with `Utf8JsonReader(line, isFinalBlock: true, default)`, and node arrays grow on demand. There is no count pass, no second IO pass, and no upfront "total elements" number (progress is reported as bytes-consumed / total). The UI thread attaches to the document immediately and the first rows appear as soon as the first chunk's lines are linked into the synthetic root and `Publish()`'d. A 13 GiB JSONL still finishes its full load in roughly the same wall-clock time as the old two-pass approach, but the user sees content within ~1 second.

### Sorting object keys

Object keys still need stable alphabetical display order. A boxed-tree loader can sort key strings collected from each object after the object has been decoded.

Jesnote does the same but defers the sort to `EndObject` and sorts the collected child id list, comparing via a struct `KeyComparer` that reads `Keys[i]` for each id. `CollectionsMarshal.AsSpan(list).Sort(comparer)` avoids the lambda closure allocation that `List.Sort(Comparison)` would incur. For a 100M-element document with many objects, this saves on the order of N small allocations.

### Progress and cancellation cadence

Progress and cancellation use a fixed 10000-node cadence in the parser. The reports are delivered to the Avalonia UI through `IProgress<ProgressInfo>`.

Jesnote uses the same 10000 tick, but two implementation details matter:

UI dispatch is not free. If a future loading dialog mirrors every parser progress report onto the UI thread, 10000 reports can still flood rendering. Throttle step-3 in-progress reports and always show step transitions plus the final `Progress >= 1.0`.

The cadence check itself must be a bucket-diff when `Count` updates in batches (as in the JSONL paths) rather than a modulo. See the Progress reporting section above for the full story; a uniform-line JSONL file with 17 tokens per line never lands `Count` on a multiple of 10 000 and the modulo never fires.

### Children: list per parent vs linked list

A parent-to-children map with one child list per parent is expensive for a 100M-element document with mostly singleton parents, which is typical of array-heavy JSONL. The map overhead alone can be hundreds of MB.

Jesnote stores children as a linked list (`FirstChild` + `NextSibling`). No per-parent map or slice header. The cost is that "get the Nth child" or "count children" walks the list, but neither operation is hot. The tree control accesses children in order via `FirstChild` then iteratively `NextSibling`, which is exactly what the linked-list layout optimizes for.

### Primitives boxed vs discriminated

A boxed-value design stores primitive values as object references. Every number and boolean pays object/interface overhead and adds GC tracking. With tens of millions of primitive values, this is a significant slice of heap.

Jesnote stores all primitive values in a single `long[] Values` slot whose interpretation is driven by `Types[i]`. No boxing. The trade-off is that all numbers are stored as `double` (acceptable for a viewer) and that strings need an extra indirection through `StringPool`. The cost of the indirection is one extra cache miss on string display; the saving is roughly 16 bytes per non-string primitive.

### Things that look like wins but are not

`MemoryMappedFile` for the in-memory path: the OS page cache is already free under the second-pass read of a `byte[]` whose contents came from the same file. mmap saves the ~1 GiB managed heap allocation during build but introduces unsafe spans. The trade is not worth it at current scale; revisit if profiling shows the managed `byte[]` as the bottleneck.

Parsing on multiple threads: `Utf8JsonReader` is inherently sequential because tokens depend on the surrounding context. You can split a JSONL file at line boundaries and parse lines in parallel, but the synchronization to insert into shared parallel arrays would dominate the savings unless the per-line work were substantial (it is not - each line is small).

`Span<int>` instead of `List<int>` for frame children: `List<int>` is needed for the doubling growth pattern. Using `int[]` directly forces pre-sizing or manual `Array.Resize`. We tried it; the build code became unreadable for no measurable gain. The pool replacement on capacity > 1024 is the meaningful fix.

`gcAllowVeryLargeObjects`: enabled by default on 64-bit .NET 8. No-op to set explicitly. Per-array length is still capped at `int.MaxValue` (~2.1 GiB), which is why the in-memory path limits at 1 GiB and the streaming path takes over.

## Lessons learned the hard way

1. `JsonReaderOptions.AllowMultipleValues` is .NET 9, not .NET 8. The codebase targets `net8.0`; if you need multi-value parsing, use line splitting.
2. `List.Clear()` keeps the backing capacity. Pooled lists that have grown large must be replaced, not cleared.
3. UI dispatch is not free. Tens of thousands of unthrottled progress updates can freeze the UI thread.
4. `Count % N == 0` only fires when `Count` is a multiple. If `Count` jumps by varying amounts, use a bucket-diff.
5. `Utf8JsonReader.GetString()` on a 100 MB string token works fine, but rendering that much text at once does not. Cap display, keep the raw value for copy/export.
6. `byte[].Length` is capped at `int.MaxValue` (~2.1 GiB). On 64-bit .NET 8 this is the default array max even with `gcAllowVeryLargeObjects` (which is on by default). Files larger than 2 GiB cannot use the in-memory path.
7. Server GC retains committed memory aggressively. After a large load, the working set stays near peak even when half the allocations are garbage. [MainWindow.ReleaseParserGarbage](src/MainWindow.cs) forces a full GC + LOH compaction at end of load, surfaces a localized "Optimizing memory..." status before it runs (the collection is stop-the-world and freezes the UI for 1-2 s on a multi-GB heap), and the final load-complete status reports `GC.GetTotalMemory(false)` so users see the real managed-heap number instead of a stale working-set figure.
8. `Utf8JsonReader.CopyString(Span<byte>)` writes the unescaped UTF-8 form into a caller-supplied buffer with no `string` allocation. Compact storage uses this to skip per-value string materialization during parse. For escaped strings the returned byte count may be smaller than `ValueSpan.Length`; reclaim the slack by rewinding the chunk's write cursor before storing the encoded ref.

That is the critical surface. Anything not covered here, infer from the code. Anything contradicted by the code, the code is the source of truth and this file is stale: please update it.
