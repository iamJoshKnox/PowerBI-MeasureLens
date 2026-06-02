# PBI Measure Lens

A lightweight Power BI Desktop helper for two everyday auditing pains:

1. **See the real measure behind a renamed visual field.** When a measure is dropped into a
   visual and given a friendlier header, the original name is only visible on hover. PBI Measure
   Lens lists every visual in a report and shows each field's **original name → display name**,
   with one-click **copy** (Excel-pasteable) and **CSV export**.
2. **Trace a measure's DAX without spelunking the model.** Pick a measure and see its DAX plus a
   **recursive dependency tree** — the measures it references, the measures *those* reference, and
   so on — read directly from your local `.pbip` semantic model files (works across chained models).

It runs as a standalone `.exe` and can register itself in the **External Tools** ribbon.

## How it works (and its limits)

- The rename mapping is read from the report's `.pbix` layout — so **save the report first**;
  the tool reads the saved file, not the live (unsaved) canvas.
- Visuals are chosen from a **list** the tool builds from the file (not by clicking the canvas).
- DAX is read from local `.pbip` semantic-model files. Reports that live-connect to the Service
  still work, because you point the tool at the same model on disk. References that resolve only
  to a **Service-only** model (not on disk) are shown as *unresolved / external*.

## Build & run

Requires the **.NET 8 SDK**.

```powershell
# Run from source
dotnet run --project src/PbiMeasureLens

# Or publish a self-contained single-file exe (end users need nothing installed)
dotnet publish src/PbiMeasureLens -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
# -> publish\PbiMeasureLens.exe
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
  Services/PbixLayoutReader.cs    parse .pbix layout -> visuals + renames
  Services/TmdlModelReader.cs     parse .pbip TMDL -> measures + columns
  Services/DependencyResolver.cs  recursive measure dependency tree
tooling/                   External Tools registration (.pbitool.json + install script)
```

## License

MIT — see [LICENSE](LICENSE).
