using TrayTrigger.Models;

namespace TrayTrigger.ViewModels;

/// <summary>One row in Settings' list of permanently-ignored game candidate paths.</summary>
public class IgnoredGamePathRowViewModel : ViewModelBase
{
    public IgnoredGamePath Model { get; }
    public string Name => Model.Name;
    public string DisplayPath => Model.ExePath
        ?? (Model.SteamAppId != null ? $"Steam AppId {Model.SteamAppId}" : null)
        ?? (Model.GogGameId != null ? $"GOG GameId {Model.GogGameId}" : null)
        ?? (Model.EaContentId != null ? $"EA ContentId {Model.EaContentId}" : null)
        ?? (Model.EpicAppName != null ? $"Epic AppName {Model.EpicAppName}" : null)
        ?? (Model.UbisoftGameId != null ? $"Ubisoft GameId {Model.UbisoftGameId}" : null)
        ?? $"Xbox {Model.XboxAumid}";

    public IgnoredGamePathRowViewModel(IgnoredGamePath model)
    {
        Model = model;
    }
}
