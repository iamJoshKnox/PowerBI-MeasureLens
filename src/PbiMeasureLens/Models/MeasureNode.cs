using System.Collections.ObjectModel;

namespace PbiMeasureLens.Models;

public enum DependencyKind
{
    Measure,
    Column,
    Unresolved, // referenced by name but not found locally (e.g. lives in a Service-only model)
    Cycle       // measure already on the current dependency path
}

/// <summary>A node in a measure's recursive dependency tree.</summary>
public sealed class MeasureNode
{
    public string Name { get; init; } = "";
    public string Table { get; init; } = "";
    public string Expression { get; init; } = "";
    public string SourceModel { get; init; } = "";
    public DependencyKind Kind { get; init; }
    /// <summary>Number of measures sharing this name across models; >1 means the resolution is a guess.</summary>
    public int DefinitionCount { get; init; } = 1;
    public ObservableCollection<MeasureNode> Children { get; } = new();

    public bool IsMeasure => Kind == DependencyKind.Measure;
    public bool IsAmbiguous => DefinitionCount > 1;

    public string Display => Kind switch
    {
        DependencyKind.Measure => (string.IsNullOrEmpty(Table) ? $"[{Name}]" : $"{Table}[{Name}]")
                                  + (IsAmbiguous
                                        ? $"  ⚠ {DefinitionCount} models define this — using {(string.IsNullOrEmpty(SourceModel) ? "first" : SourceModel)}"
                                        : ""),
        DependencyKind.Column => string.IsNullOrEmpty(Table) ? $"[{Name}]  (column)" : $"{Table}[{Name}]  (column)",
        DependencyKind.Unresolved => $"[{Name}]  (unresolved / external)",
        DependencyKind.Cycle => $"[{Name}]  (circular reference)",
        _ => Name
    };
}
