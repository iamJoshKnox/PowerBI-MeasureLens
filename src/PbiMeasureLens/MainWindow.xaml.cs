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

        var view = new ListCollectionView(_visuals);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(VisualInfo.Page)));
        VisualsList.ItemsSource = view;

        FieldsGrid.ItemsSource = null;
        ClearDax();

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
        ClearDax();
    }

    private void LoadModel()
    {
        _model = TmdlModelReader.Load(_settings.SemanticModelRoots);
    }

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

    // ---- Selection ----

    private void VisualsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        FieldsGrid.ItemsSource = (VisualsList.SelectedItem as VisualInfo)?.Fields;
        ClearDax();
    }

    private void FieldsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FieldsGrid.SelectedItem is not FieldMapping field)
        {
            ClearDax();
            return;
        }

        if (field.Kind != FieldKind.Measure)
        {
            DepTree.ItemsSource = null;
            DaxText.Text = $"\"{field.OriginalName}\" is a column — columns have no DAX expression.";
            return;
        }

        if (_model == null || _model.MeasureCount == 0)
        {
            DepTree.ItemsSource = null;
            DaxText.Text =
                "No semantic-model measures loaded.\n\n" +
                "Use “Add model folder…” above and point it at the OneDrive folder that " +
                "contains your .pbip semantic model(s).";
            return;
        }

        var root = DependencyResolver.Build(field.OriginalName, _model);
        DepTree.ItemsSource = new[] { root };

        DaxText.Text = root.Kind == DependencyKind.Unresolved
            ? $"Measure \"{field.OriginalName}\" was not found in the configured semantic model(s).\n\n" +
              "It may live in a Service-only model that isn't on disk, or under a different name."
            : DescribeMeasure(root);

        if (_model.HasDuplicate(field.OriginalName))
            DaxText.Text += "\n\n⚠ Note: more than one measure named this exists across the scanned models; showing the first.";
    }

    private void DepTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is MeasureNode node && node.IsMeasure)
            DaxText.Text = DescribeMeasure(node);
    }

    private static string DescribeMeasure(MeasureNode node)
    {
        var header = string.IsNullOrEmpty(node.Table) ? $"[{node.Name}]" : $"{node.Table}[{node.Name}]";
        return $"{header} =\n\n{node.Expression}";
    }

    private void ClearDax()
    {
        DepTree.ItemsSource = null;
        DaxText.Text = "Select a measure in the Fields grid to see its DAX.";
    }

    // ---- Copy / export ----

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var fields = CurrentFields();
        if (fields.Count == 0) { MessageBox.Show("No fields to copy.", "Copy"); return; }

        var sb = new StringBuilder();
        sb.AppendLine("Original\tDisplay\tKind\tRenamed");
        foreach (var f in fields)
            sb.AppendLine($"{f.OriginalName}\t{f.DisplayName}\t{f.KindText}\t{f.RenamedText}");

        try
        {
            Clipboard.SetText(sb.ToString());
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not access the clipboard: " + ex.Message, "Copy");
        }
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var fields = CurrentFields();
        if (fields.Count == 0) { MessageBox.Show("No fields to export.", "Export CSV"); return; }

        var dlg = new SaveFileDialog
        {
            Title = "Export field mapping",
            Filter = "CSV file (*.csv)|*.csv",
            FileName = "visual-field-mapping.csv"
        };
        if (dlg.ShowDialog() != true) return;

        var sb = new StringBuilder();
        sb.AppendLine("Original,Display,Kind,Renamed");
        foreach (var f in fields)
            sb.AppendLine(string.Join(",", Csv(f.OriginalName), Csv(f.DisplayName), Csv(f.KindText), Csv(f.RenamedText)));

        try
        {
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not write file: " + ex.Message, "Export CSV");
        }
    }

    private List<FieldMapping> CurrentFields()
        => (VisualsList.SelectedItem as VisualInfo)?.Fields ?? new List<FieldMapping>();

    private static string Csv(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}
