# PBI Measure Lens

A lightweight Power BI Desktop helper for two everyday auditing pains:

1. **See the real measure behind a renamed visual field.** When a measure is dropped into a
   visual and given a friendlier header, the original name is only visible on hover. PBI Measure
   Lens lists every visual in a report and shows each field's **original name → display name**,
   with one-click **copy** (Excel-pasteable) and **CSV export**. A **whole-report** toggle plus
   **search** and **"only renamed"** filters turn this into a full rename audit across all pages.
2. **Trace a measure's DAX without spelunking the model.** Pick a measure and see its DAX plus a
   **recursive dependency tree** — the measures it references, the measures *those* reference, and
   so on — read directly from your local `.pbip` semantic model files. It resolves references
   **across chained/composite models** and labels each measure with the **source model** it lives in.
3. **See a measure's footprint.** For any measure, the **Footprint** tab shows every visual/page
   that displays it (and the display name used in each — flagging when it's **renamed
   inconsistently across pages**), plus which **other measures reference it** (reverse lineage).

It runs as a standalone `.exe` and can register itself in the **External Tools** ribbon.

## How it works (and its limits)

- The rename mapping is read from the saved report — so **save the report first**; the tool reads
  the saved file, not the live (unsaved) canvas.
- Both report formats are supported and auto-detected: the **classic** `.pbix` layout
  (`Report/Layout`) and the modern **PBIR / enhanced report format** — a `.pbip` project, a
  `.Report` folder, a `definition.pbir` file, or a newer `.pbix` that embeds the PBIR definition.
- A status line reports what was parsed (`N visual(s) · M with fields`), and you get a warning
  instead of a silent empty list when part of a report can't be read.
- Visuals are chosen from a **list** the tool builds from the file (not by clicking the canvas).
- DAX is read from local `.pbip` semantic-model files. Reports that live-connect to the Service
  still work, because you point the tool at the same model on disk. References that resolve only
  to a **Service-only** model (not on disk) are shown as *unresolved / external*.

## Build & run

Requires the **.NET 8 SDK**.

```powershell
# Run from source
dotnet run --project src/PbiMeasureLens

# Or produce both distribution builds at once
tooling\publish.ps1
```

### Distribution builds

| Build | Output | Size | Needs on target |
|---|---|---|---|
| **Self-contained** (compressed single file) | `publish\PbiMeasureLens.exe` | ~68 MB | Nothing — just copy & run |
| **Framework-dependent** (single file) | `publish-fd\PbiMeasureLens.exe` | ~0.3 MB | **.NET 8 Desktop Runtime** installed |

Use **self-contained** for sharing / copying to VMs (the runtime + WPF are bundled, so there are
no prerequisites; first launch decompresses once to a temp folder). Use **framework-dependent**
for standardized machines you know already have the .NET 8 **Desktop** Runtime
(`winget install Microsoft.DotNet.DesktopRuntime.8`).

Build them individually if you prefer:

```powershell
# Self-contained, compressed
dotnet publish src/PbiMeasureLens -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish

# Framework-dependent
dotnet publish src/PbiMeasureLens -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=false -o publish-fd
```

## Register in the External Tools ribbon (optional)

From an **elevated** PowerShell (writes into Program Files):

```powershell
tooling\install-external-tool.ps1            # uses publish\PbiMeasureLens.exe
# or
tooling\install-external-tool.ps1 -ExePath "C:\Tools\PbiMeasureLens\PbiMeasureLens.exe"
```

Restart Power BI Desktop; the **PBI Measure Lens** button appears under External Tools. (Power BI
can't hand the report path to a tool, so the app still opens a file picker on launch.)

## Usage

1. **Open .pbix…** and choose a saved report.
2. Pick a visual → the **Fields** grid shows Original / Display / Kind / Renamed. Use **Copy table**
   or **Export CSV…**.
3. **Add model folder…** and point at the OneDrive folder containing your `.pbip` semantic
   model(s). Select a measure row → its **DAX** and a **dependency tree** appear; click any measure
   node to view its DAX. Settings (last report, model folders) are remembered.

## Project layout

```
src/PbiMeasureLens/        WPF app (.NET 8)
  Services/ReportReader.cs        detect format (classic vs PBIR) -> dispatch
  Services/PbixLayoutReader.cs    parse classic .pbix layout -> visuals + renames
  Services/PbirReportReader.cs    parse PBIR definition (visual.json) -> visuals + renames
  Services/TmdlModelReader.cs     parse .pbip TMDL -> measures + columns
  Services/DependencyResolver.cs  recursive measure dependency tree
tooling/                   External Tools registration (.pbitool.json + install script)
```

## License

MIT — see [LICENSE](LICENSE).
