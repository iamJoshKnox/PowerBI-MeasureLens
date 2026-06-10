using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using PbiMeasureLens.Models;
using PbiMeasureLens.Services;

namespace PbiMeasureLens;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private List<VisualInfo> _visuals = new();
    private List<FieldMapping> _allFields = new();
    private TmdlModel? _model;

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsStore.Load();

        LoadModel();
        UpdateModelInfo();
        ClearMeasureDetail();

        if (!string.IsNullOrEmpty(_settings.LastPbixPath) && File.Exists(_settings.LastPbixPath))
            LoadPbix(_settings.LastPbixPath);
    }

    // ---- Report (.pbix) ----

    private void OpenPbix_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open a Power BI report",
            Filter = "Power BI report (*.pbix;*.pbip;*.pbir)|*.pbix;*.pbip;*.pbir|All files (*.*)|*.*"
        };
        if (!string.IsNullOrEmpty(_settings.LastPbixPath))
            dlg.InitialDirectory = Path.GetDirectoryName(_settings.LastPbixPath) ?? "";

        if (dlg.ShowDialog() == true)
            LoadPbix(dlg.FileName);
    }

    private void LoadPbix(string path)
    {
        PbixReadResult result;
        try
        {
            result = ReportReader.ReadVisuals(path);
        }
        catch (PbixReadException ex)
        {
            MessageBox.Show(ex.Message, "Could not read report", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _visuals = result.Visuals;
        _allFields = _visuals.SelectMany(v => v.Fields).ToList();

        RefreshVisualsView();
        ClearMeasureDetail();
        RefreshFieldsView();

        PbixPathText.Text = path;
        PbixStatusText.Text = DescribeRead(result);
        if (result.Warning != null)
            MessageBox.Show(result.Warning, "Report read incomplete", MessageBoxButton.OK, MessageBoxImage.Warning);

        _settings.LastPbixPath = path;
        SettingsStore.Save(_settings);
    }

    private static string DescribeRead(PbixReadResult r)
    {
        var msg = $"{r.Visuals.Count} visual(s) · {r.DataVisuals} with fields";
        if (r.SkippedMalformed > 0) msg += $" · {r.SkippedMalformed} unreadable";
        if (r.UnknownSchema > 0) msg += $" · {r.UnknownSchema} newer-format (skipped)";
        return msg;
    }

    // ---- Semantic model folders ----

    private void AddModelFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select a folder containing your .pbip semantic model(s)" };
        var initial = LastModelBrowseDir();
        if (initial != null) dlg.InitialDirectory = initial;

        if (dlg.ShowDialog() == true)
            AddModelRoots(new[] { dlg.FolderName });
    }

    /// <summary>
    /// Where the model pickers should open: the *parent* of the last-added model, since semantic
    /// models usually sit side by side in one folder — so the next pick is one click away.
    /// </summary>
    private string? LastModelBrowseDir()
    {
        if (_settings.SemanticModelRoots.Count == 0) return null;
        var last = _settings.SemanticModelRoots[^1];
        var parent = Path.GetDirectoryName(last.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent)) return parent;
        return Directory.Exists(last) ? last : null;
    }

    private void AddModelFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select a semantic model (.pbip / .pbir)",
            Filter = "Power BI model/project (*.pbip;*.pbir)|*.pbip;*.pbir|All files (*.*)|*.*"
        };
        var initial = LastModelBrowseDir();
        if (initial != null) dlg.InitialDirectory = initial;

        if (dlg.ShowDialog() == true)
        {
            var roots = ResolveModelRootsFromFile(dlg.FileName);
            if (roots.Count == 0)
                MessageBox.Show(
                    "No *.SemanticModel folder was found next to that file.\n\n" +
                    "Pick the model's .pbip/.pbir, or use “Add model folder…”.",
                    "No model found", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                AddModelRoots(roots);
        }
    }

    /// <summary>Resolve the *.SemanticModel folder(s) near a selected .pbip/.pbir file.</summary>
    private static List<string> ResolveModelRootsFromFile(string filePath)
    {
        string dir = Path.GetDirectoryName(filePath) ?? "";
        var found = new List<string>();
        if (!Directory.Exists(dir)) return found;

        // Siblings first (the usual "<Name>.pbip + <Name>.SemanticModel" layout), then a shallow search.
        try { found.AddRange(Directory.EnumerateDirectories(dir, "*.SemanticModel")); } catch { }
        if (found.Count == 0)
            try { found.AddRange(Directory.EnumerateDirectories(dir, "*.SemanticModel", SearchOption.AllDirectories)); } catch { }

        // Fall back to the containing folder; the scanner searches it recursively.
        return found.Count > 0 ? found.Distinct(StringComparer.OrdinalIgnoreCase).ToList() : new List<string> { dir };
    }

    private void AddModelRoots(IEnumerable<string> roots)
    {
        bool added = false;
        foreach (var root in roots)
        {
            if (!_settings.SemanticModelRoots.Contains(root, StringComparer.OrdinalIgnoreCase))
            {
                _settings.SemanticModelRoots.Add(root);
                added = true;
            }
        }
        if (!added) return;

        SettingsStore.Save(_settings);
        LoadModel();
        UpdateModelInfo();
    }

    private void RemoveModel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string path }) return;
        _settings.SemanticModelRoots.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        SettingsStore.Save(_settings);
        LoadModel();
        UpdateModelInfo();
        ClearMeasureDetail();
    }

    private void ClearModelFolders_Click(object sender, RoutedEventArgs e)
    {
        _settings.SemanticModelRoots.Clear();
        SettingsStore.Save(_settings);
        LoadModel();
        UpdateModelInfo();
        ClearMeasureDetail();
    }

    private void LoadModel() => _model = TmdlModelReader.Load(_settings.SemanticModelRoots);

    private void UpdateModelInfo()
    {
        ModelChips.ItemsSource = _settings.SemanticModelRoots
            .Select(p => new ModelRootItem { Path = p, Name = NiceModelName(p) })
            .ToList();

        if (_settings.SemanticModelRoots.Count == 0)
        {
            ModelInfoText.Text = "No semantic-model folders configured (DAX lookup disabled).";
            return;
        }

        int models = _model?.ScannedModelFolders.Count ?? 0;
        int measures = _model?.MeasureCount ?? 0;
        ModelInfoText.Text =
            $"{_settings.SemanticModelRoots.Count} root(s) · {models} model(s) · {measures} measure(s)";
    }

    private static string NiceModelName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        const string suffix = ".SemanticModel";
        if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            name = name[..^suffix.Length];
        return string.IsNullOrEmpty(name) ? path : name;
    }

    // ---- Visuals list ----

    private void RefreshVisualsView()
    {
        IEnumerable<VisualInfo> source = HideSlicers.IsChecked == true
            ? _visuals.Where(v => !v.IsSlicer)
            : _visuals;

        var view = new ListCollectionView(source.ToList());
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(VisualInfo.Page)));
        VisualsList.ItemsSource = view;
    }

    private void HideSlicers_Changed(object sender, RoutedEventArgs e)
    {
        RefreshVisualsView();
        RefreshFieldsView();
    }

    // ---- Fields grid: source + filtering ----

    private void VisualsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshFieldsView();

    private void Filter_Changed(object sender, RoutedEventArgs e) => RefreshFieldsView();

    private void RefreshFieldsView()
    {
        bool whole = WholeReport.IsChecked == true;
        bool hideSlicers = HideSlicers.IsChecked == true;
        IEnumerable<FieldMapping> source = whole
            ? _allFields
            : (VisualsList.SelectedItem as VisualInfo)?.Fields ?? Enumerable.Empty<FieldMapping>();

        if (whole && hideSlicers)
            source = source.Where(f => f.VisualType.IndexOf("slicer", StringComparison.OrdinalIgnoreCase) < 0);

        string search = SearchBox.Text?.Trim() ?? "";
        var matches = BuildMatcher(search); // compiled once per refresh, reused for every row/field

        var view = new ListCollectionView(source.ToList())
        {
            Filter = o =>
            {
                if (o is not FieldMapping f) return false;
                if (search.Length == 0) return true;
                return matches(f.OriginalName) || matches(f.DisplayName);
            }
        };
        FieldsGrid.ItemsSource = view;
    }

    /// <summary>
    /// Build a case-insensitive matcher for the search box. A '*' acts as a wildcard
    /// matching any run of characters (e.g. "Budget*Pounds"); without one it's a plain
    /// substring search. The pattern is compiled once and reused across all rows.
    /// </summary>
    private static Func<string, bool> BuildMatcher(string search)
    {
        if (search.Length == 0) return _ => true;
        if (!search.Contains('*')) return s => Contains(s, search);

        // Glob: split on '*', escape the literal pieces, rejoin with ".*". Unanchored so it
        // still matches anywhere in the value (consistent with the plain substring behaviour).
        string pattern = string.Join(".*", search.Split('*').Select(Regex.Escape));
        var rx = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return s => rx.IsMatch(s);
    }

    private static bool Contains(string haystack, string needle)
        => haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

    // ---- Measure detail (DAX + footprint) ----

    private void FieldsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FieldsGrid.SelectedItem is not FieldMapping field)
        {
            ClearMeasureDetail();
            return;
        }

        if (field.Kind != FieldKind.Measure)
        {
            DepTree.ItemsSource = null;
            ShowDaxMessage($"\"{field.Qualified}\" is a column — columns have no DAX expression.");
            ShowFootprint(field.OriginalName); // still useful: where else is this displayed
            return;
        }

        if (_model == null || _model.MeasureCount == 0)
        {
            DepTree.ItemsSource = null;
            ShowDaxMessage(
                "No semantic-model measures loaded.\n\n" +
                "Use “Add model folder…” or “Add model file…” above and point at the folder(s) that " +
                "contain your .pbip semantic model(s).");
        }
        else
        {
            var root = DependencyResolver.Build(field.OriginalName, _model);
            DepTree.ItemsSource = new[] { root };
            if (root.Kind == DependencyKind.Unresolved)
                ShowDaxMessage(
                    $"Measure \"{field.OriginalName}\" was not found in the configured semantic model(s).\n\n" +
                    "It may live in a Service-only model that isn't on disk, or under a different name.");
            else
                ShowDaxMeasure(root);
        }

        ShowFootprint(field.OriginalName);
    }

    private void DepTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is MeasureNode node && node.IsMeasure)
        {
            ShowDaxMeasure(node);
            ShowFootprint(node.Name);
        }
    }

    private void ShowFootprint(string measureName)
    {
        var usages = _allFields
            .Where(f => f.Kind == FieldKind.Measure &&
                        string.Equals(f.OriginalName, measureName, StringComparison.OrdinalIgnoreCase))
            .Select(f => new MeasureUsage { Page = f.Page, Visual = f.VisualLabel, DisplayName = f.DisplayName })
            .ToList();

        UsageGrid.ItemsSource = usages;

        if (usages.Count == 0)
        {
            FootprintSummary.Text = $"“{measureName}” is not displayed directly in any visual in this report.";
        }
        else
        {
            int pages = usages.Select(u => u.Page).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            int names = usages.Select(u => u.DisplayName).Distinct(StringComparer.Ordinal).Count();
            var msg = $"Displayed in {usages.Count} visual(s) across {pages} page(s); {names} distinct display name(s).";
            if (names > 1) msg += "  ⚠ Renamed inconsistently across the report.";
            FootprintSummary.Text = msg;
        }

        if (_model != null && _model.MeasureCount > 0)
        {
            var refs = DependencyResolver.FindReferencingMeasures(measureName, _model)
                .Select(d => string.IsNullOrEmpty(d.ModelName)
                    ? $"{d.Table}[{d.Name}]"
                    : $"{d.Table}[{d.Name}]   —   {d.ModelName}")
                .ToList();
            RefByList.ItemsSource = refs.Count > 0 ? refs : new List<string> { "(no other measures reference it)" };
        }
        else
        {
            RefByList.ItemsSource = null;
        }
    }

    private void ShowDaxMessage(string message) => DaxBox.Document = DaxHighlighter.BuildMessage(message);

    private void ShowDaxMeasure(MeasureNode node)
    {
        var header = $"[{node.Name}] ="; // a measure is referenced bare, never table-qualified
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(node.SourceModel)) parts.Add($"model: {node.SourceModel}");
        if (!string.IsNullOrEmpty(node.Table)) parts.Add($"table: {node.Table}");
        string? model = parts.Count > 0 ? "(" + string.Join("; ", parts) + ")" : null;

        string? note = node.IsAmbiguous
            ? $"⚠ {node.DefinitionCount} measures share this name across the scanned models; " +
              $"showing the one from {(string.IsNullOrEmpty(node.SourceModel) ? "the first model" : node.SourceModel)}."
            : null;

        DaxBox.Document = DaxHighlighter.BuildMeasure(header, node.Expression, note, model);
    }

    private void ClearMeasureDetail()
    {
        DepTree.ItemsSource = null;
        ShowDaxMessage("Select a measure in the Fields grid to see its DAX.");
        FootprintSummary.Text = "Select a measure to see where it is used across the report.";
        UsageGrid.ItemsSource = null;
        RefByList.ItemsSource = null;
    }

    // ---- Copy / export (operate on the rows currently shown in the grid) ----

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var rows = CurrentRows();
        if (rows.Count == 0) { MessageBox.Show("No rows to copy.", "Copy"); return; }

        var sb = new StringBuilder();
        sb.AppendLine("Page\tVisual\tTable\tOriginal\tDisplay\tKind");
        foreach (var f in rows)
            sb.AppendLine($"{f.Page}\t{f.VisualLabel}\t{f.Table}\t{f.OriginalName}\t{f.DisplayName}\t{f.KindText}");

        try { Clipboard.SetText(sb.ToString()); }
        catch (Exception ex) { MessageBox.Show("Could not access the clipboard: " + ex.Message, "Copy"); }
    }

    private void CopyWithDax_Click(object sender, RoutedEventArgs e)
    {
        var rows = CurrentRows();
        if (rows.Count == 0) { MessageBox.Show("No rows to copy.", "Copy"); return; }

        var sb = new StringBuilder();
        sb.AppendLine("Page\tVisual\tTable\tOriginal\tDisplay\tKind\tDAX");
        foreach (var f in rows)
        {
            string dax = f.Kind == FieldKind.Measure
                ? _model?.FindMeasure(f.OriginalName)?.Expression ?? ""
                : "";
            sb.AppendLine(string.Join("\t",
                f.Page, f.VisualLabel, f.Table, f.OriginalName, f.DisplayName, f.KindText, TsvCell(dax)));
        }

        try { Clipboard.SetText(sb.ToString()); }
        catch (Exception ex) { MessageBox.Show("Could not access the clipboard: " + ex.Message, "Copy"); }
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var rows = CurrentRows();
        if (rows.Count == 0) { MessageBox.Show("No rows to export.", "Export CSV"); return; }

        var dlg = new SaveFileDialog
        {
            Title = "Export field mapping",
            Filter = "CSV file (*.csv)|*.csv",
            FileName = WholeReport.IsChecked == true ? "report-field-audit.csv" : "visual-field-mapping.csv"
        };
        if (dlg.ShowDialog() != true) return;

        var sb = new StringBuilder();
        sb.AppendLine("Page,Visual,Table,Original,Display,Kind");
        foreach (var f in rows)
            sb.AppendLine(string.Join(",", Csv(f.Page), Csv(f.VisualLabel), Csv(f.Table), Csv(f.OriginalName), Csv(f.DisplayName), Csv(f.KindText)));

        try { File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true)); }
        catch (Exception ex) { MessageBox.Show("Could not write file: " + ex.Message, "Export CSV"); }
    }

    private List<FieldMapping> CurrentRows()
        => FieldsGrid.Items.OfType<FieldMapping>().ToList();

    private static string Csv(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    /// <summary>Quote a tab-delimited clipboard cell so multi-line/tabbed DAX pastes into one Excel cell.</summary>
    private static string TsvCell(string value)
    {
        if (value.Contains('\t') || value.Contains('\n') || value.Contains('\r') || value.Contains('"'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}
