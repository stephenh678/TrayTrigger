using TrayTrigger.Models;

namespace TrayTrigger.ViewModels;

/// <summary>One row in Settings' list of permanently-ignored game candidate paths.</summary>
public class IgnoredGamePathRowViewModel : ViewModelBase
{
    public IgnoredGamePath Model { get; }
    public string Name => Model.Name;
    public string DisplayPath => Model.ExePath ?? $"Steam AppId {Model.SteamAppId}";

    public IgnoredGamePathRowViewModel(IgnoredGamePath model)
    {
        Model = model;
    }
}
