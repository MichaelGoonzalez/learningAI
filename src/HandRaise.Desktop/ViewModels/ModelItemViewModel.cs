using CommunityToolkit.Mvvm.ComponentModel;
using HandRaise.Domain.Models;

namespace HandRaise.Desktop.ViewModels;

public sealed class ModelItemViewModel : ObservableObject
{
    public ModelItemViewModel(ModelDescriptor descriptor)
    {
        Id = descriptor.Id;
        DisplayName = descriptor.DisplayName;
        Version = descriptor.Version;
        Format = descriptor.Format.ToString().ToUpperInvariant();
        CapabilitiesSummary = descriptor.Capabilities.Count > 0
            ? string.Join(", ", descriptor.Capabilities.Select(c => c.ToString()))
            : "General";
        Resolution = $"{descriptor.InputWidth}x{descriptor.InputHeight}";
        MemoryMb = $"{descriptor.MemoryEstimateMb:0.#} MB";
        PreferredDevice = descriptor.PreferredDevice ?? "DirectML / Auto";
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string Version { get; }
    public string Format { get; }
    public string CapabilitiesSummary { get; }
    public string Resolution { get; }
    public string MemoryMb { get; }
    public string PreferredDevice { get; }
}
