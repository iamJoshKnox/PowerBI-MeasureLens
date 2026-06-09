namespace PbiMeasureLens.Models;

/// <summary>A configured semantic-model root folder, shown as a removable chip in the toolbar.</summary>
public sealed class ModelRootItem
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
}
