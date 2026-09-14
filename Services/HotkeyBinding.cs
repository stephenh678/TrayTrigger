using System.Collections.Generic;
using System.Linq;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>What a registered hotkey launches, so <see cref="HotkeyManager"/> can raise the right event.</summary>
public enum HotkeyOwnerKind
{
    Game,
    Tool
}

/// <summary>
/// One launch hotkey to register: who owns it, the text of the combo, and whether it launches a game
/// or a tool. Lets <see cref="HotkeyManager"/> register every launch hotkey in TrayTrigger from one
/// list without depending on <see cref="GameEntry"/> (the planned Tools section supplies its own).
/// </summary>
/// <param name="OwnerId">The entry's id; raised with the triggered event and used by the recorder's conflict check.</param>
/// <param name="OwnerName">Named in logs and in "Already used to launch ..." messages.</param>
/// <param name="Hotkey">The combo as stored ("Ctrl+Alt+G"); blank means none.</param>
public sealed record HotkeyBinding(string OwnerId, string OwnerName, string Hotkey, HotkeyOwnerKind Kind)
{
    public static HotkeyBinding ForGame(GameEntry game) =>
        new(game.Id, game.Name, game.Hotkey, HotkeyOwnerKind.Game);

    /// <summary>
    /// The bindings that have a hotkey, games first and then tools, each keeping its given order.
    /// Windows grants a combo to the first registration, so a game always keeps a combo a tool also
    /// uses, and within one kind the earlier entry wins, as it always has for games.
    /// </summary>
    internal static IEnumerable<HotkeyBinding> InRegistrationOrder(IEnumerable<HotkeyBinding> bindings) =>
        bindings
            .Where(b => !string.IsNullOrWhiteSpace(b.Hotkey))
            .OrderBy(b => b.Kind == HotkeyOwnerKind.Game ? 0 : 1);
}
