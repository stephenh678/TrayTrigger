using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using TrayTrigger.Services;

namespace TrayTrigger.ViewModels;

/// <summary>
/// One line of About > Shortcuts' list of game and tool hotkeys: the key, whose it is, and (when Windows
/// would not give it to them) why it doesn't work. <see cref="EditCommand"/> opens that game's or tool's
/// editor, where the hotkey is set.
/// </summary>
public sealed record LaunchHotkeyRow(string Hotkey, string Name, string KindLabel, string Note, ICommand EditCommand)
{
    public bool HasNote => Note.Length > 0;

    /// <summary>The note on a hotkey that could not be registered. Another game, tool or app already has the key.</summary>
    public const string NotActiveNote = "Not active: already in use";

    /// <summary>
    /// The rows for every game and tool that has a hotkey, games first and then tools, each in name order.
    /// <paramref name="inactiveOwnerIds"/> are the owners whose hotkey failed to register at the last
    /// registration (<see cref="HotkeyManager.RegisterHotkeys"/>).
    /// </summary>
    public static IReadOnlyList<LaunchHotkeyRow> Build(
        IEnumerable<(HotkeyBinding Binding, ICommand Edit)> items, ISet<string> inactiveOwnerIds) =>
        items
            .Where(i => !string.IsNullOrWhiteSpace(i.Binding.Hotkey))
            .OrderBy(i => i.Binding.Kind == HotkeyOwnerKind.Game ? 0 : 1)
            .ThenBy(i => i.Binding.OwnerName, StringComparer.CurrentCultureIgnoreCase)
            .Select(i => new LaunchHotkeyRow(
                i.Binding.Hotkey,
                i.Binding.OwnerName,
                i.Binding.Kind == HotkeyOwnerKind.Tool ? "Tool" : "Game",
                inactiveOwnerIds.Contains(i.Binding.OwnerId) ? NotActiveNote : string.Empty,
                i.Edit))
            .ToList();
}
