using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
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

        if (!string.IsNullOrEmpty(_settings.LastPbixPath) && File.Exists(_settings.LastPbixPath))
            LoadPbix(_settings.LastPbixPath);
    }

    // ---- Report (.pbix) ----

    private void OpenPbix_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open a Power BI report",
            Filter = "Power BI report (*.pbix)|*.pbix|All files (*.*)|*.*"
        };
        if (!string.IsNullOrEmpty(_settings.LastPbixPath))
            dlg.InitialDirectory = Path.GetDirectoryName(_settings.LastPbixPath) ?? "";

        if (dlg.ShowDialog() == true)
            LoadPbix(dlg.FileName);
    }

    private void LoadPbix(string path)
    {
        try
        {
            _visuals = PbixLayoutReader.ReadVisuals(path);
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

        _allFields = _visuals.SelectMany(v => v.Fields).ToList();

        var view = new ListCollectionView(_visuals);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(VisualInfo.Page)));
        VisualsList.ItemsSource = view;

        ClearMeasureDetail();
        RefreshFieldsView();

        PbixPathText.Text = path;
        _settings.LastPbixPath = path;
        SettingsStore.Save(_settings);
    }

    // ---- Semantic model folders ----

    private void AddModelFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select a folder containing your .pbip semantic model(s)" };
        if (_settings.SemanticModelRoots.Count > 0)
        {
            var last = _settings.SemanticModelRoots[^1];
            if (Directory.Exists(last)) dlg.InitialDirectory = last;
        }

        if (dlg.ShowDialog() == true)
        {
            if (!_settings.SemanticModelRoots.Contains(dlg.FolderName, StringComparer.OrdinalIgnoreCase))
                _settings.SemanticModelRoots.Add(dlg.FolderName);
            SettingsStore.Save(_settings);
            LoadModel();
            UpdateModelInfo();
        }
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
        if (_settings.SemanticModelRoots.Count == 0)
        {
            ModelInfoText.Text = "No semantic-model folders configured (DAX lookup disabled).";
            return;
        }

        int models = _model?.ScannedModelFolders.Count ?? 0;
        int measures = _model?.MeasureCount ?? 0;
        ModelInfoText.Text =
            $"{_settings.SemanticModelRoots.Count} root(s) · {models} semantic model(s) · {measures} measure(s) — " +
            string.Join("; ", _settings.SemanticModelRoots);
    }

    // ---- Fields grid: source + filtering ----

    private void VisualsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshFieldsView();

    private void Filter_Changed(object sender, RoutedEventArgs e) => RefreshFieldsView();

    private void RefreshFieldsView()
    {
        bool whole = WholeReport.IsChecked == true;
        IEnumerable<FieldMapping> source = whole
            ? _allFields
            : (VisualsList.SelectedItem as VisualInfo)?.Fields ?? Enumerable.Empty<FieldMapping>();

        string search = SearchBox.Text?.Trim() ?? "";
        bool onlyRenamed = OnlyRenamed.IsChecked == true;

        var view = new ListCollectionView(source.ToList())
        {
            Filter = o =>
            {
                if (o is not FieldMapping f) return false;
                if (onlyRenamed && !f.IsRenamed) return false;
                if (search.Length == 0) return true;
                return Contains(f.OriginalName, search) || Contains(f.DisplayName, search)
                    || Contains(f.Page, search) || Contains(f.VisualLabel, search);
            }
        };
        FieldsGrid.ItemsSource = view;
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
            DaxText.Text = $"\"{field.OriginalName}\" is a column — columns have no DAX expression.";
            ShowFootprint(field.OriginalName); // still useful: where else is this displayed
            return;
        }

        if (_model == null || _model.MeasureCount == 0)
        {
            DepTree.ItemsSource = null;
            DaxText.Text =
                "No semantic-model measures loaded.\n\n" +
                "Use “Add model folder…” above and point it at the OneDrive folder that " +
                "contains your .pbip semantic model(s).";
        }
        else
        {
            var root = DependencyResolver.Build(field.OriginalName, _model);
            DepTree.ItemsSource = new[] { root };
            DaxText.Text = root.Kind == DependencyKind.Unresolved
                ? $"Measure \"{field.OriginalName}\" was not found in the configured semantic model(s).\n\n" +
                  "It may live in a Service-only model that isn't on disk, or under a different name."
                : DescribeMeasure(root);

            if (_model.HasDuplicate(field.OriginalName))
                DaxText.Text += "\n\n⚠ Note: more than one measure named this exists across the scanned models; showing the first.";
        }

        ShowFootprint(field.OriginalName);
    }

    private void DepTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is MeasureNode node && node.IsMeasure)
        {
            DaxText.Text = DescribeMeasure(node);
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

    private static string DescribeMeasure(MeasureNode node)
    {
        var header = string.IsNullOrEmpty(node.Table) ? $"[{node.Name}]" : $"{node.Table}[{node.Name}]";
        if (!string.IsNullOrEmpty(node.SourceModel)) header += $"   (model: {node.SourceModel})";
        return $"{header} =\n\n{node.Expression}";
    }

    private void ClearMeasureDetail()
    {
        DepTree.ItemsSource = null;
        DaxText.Text = "Select a measure in the Fields grid to see its DAX.";
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
        sb.AppendLine("Page\tVisual\tOriginal\tDisplay\tKind\tRenamed");
        foreach (var f in rows)
            sb.AppendLine($"{f.Page}\t{f.VisualLabel}\t{f.OriginalName}\t{f.DisplayName}\t{f.KindText}\t{f.RenamedText}");

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
        sb.AppendLine("Page,Visual,Original,Display,Kind,Renamed");
        foreach (var f in rows)
            sb.AppendLine(string.Join(",", Csv(f.Page), Csv(f.VisualLabel), Csv(f.OriginalName), Csv(f.DisplayName), Csv(f.KindText), Csv(f.RenamedText)));

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
}
