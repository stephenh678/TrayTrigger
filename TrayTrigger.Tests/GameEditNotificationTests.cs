using System.Collections;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Input;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>
/// Every change on a game's Settings form names what changed. The form used to announce each change as
/// "everything changed" (a null property name), which made the page re-read all its bindings on every
/// tick or keystroke and threw the All tab back to the top. Naming them is only safe while every value
/// the page shows is announced when it moves, which this checks: each settable property is changed in
/// turn, and any value the page binds to that moved without being named is a field that would go stale.
/// </summary>
public sealed class GameEditNotificationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TrayTriggerEditNotify", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private GameEditViewModel NewEditor(GameEntry game) =>
        new(game, new[] { "Action", "RPG" }, new IconExtractorService(new StorageService(Path.Combine(_root, "r"), Path.Combine(_root, "l"))),
            scriptsEnabled: true);

    private static IEnumerable<(string Label, Func<GameEntry> Game)> Games() =>
    [
        ("local", () => new GameEntry { Name = "Local", Category = "Action", ExecutablePath = @"C:\Games\Local\game.exe", WorkingDirectory = @"C:\Games\Local" }),
        ("epic", () => new GameEntry { Name = "Epic", Category = "Action", ExecutablePath = @"C:\Games\Epic\game.exe", WorkingDirectory = @"C:\Games\Epic",
            IsEpicGame = true, ImportedFrom = LauncherPlatform.Epic, EpicAppName = "App" }),
        ("steam", () => new GameEntry { Name = "Steam", Category = "Action", ExecutablePath = "steam://rungameid/620", IsSteamGame = true, SteamAppId = "620" }),
        ("scripts", () => new GameEntry { Name = "Scripted", Category = "RPG", ExecutablePath = @"C:\Games\S\s.exe",
            PreLaunchScriptPath = @"C:\Scripts\pre.ps1", PostExitScriptPath = @"C:\Scripts\post.ps1" }),
    ];

    /// <summary>The names the Settings page binds to on the editor: GameEditPanel, and Game Details' footer (EditViewModel.X).</summary>
    private static HashSet<string> BoundNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(SourceTree.Read("Views/GameEditPanel.xaml"), @"\{Binding\s+(?:Path=)?([A-Za-z_]\w*)"))
            names.Add(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(SourceTree.Read("Views/GameDetailsDialog.xaml"), @"EditViewModel\.([A-Za-z_]\w*)"))
            names.Add(m.Groups[1].Value);
        return names;
    }

    private static readonly PropertyInfo[] Readable = typeof(GameEditViewModel)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.GetIndexParameters().Length == 0 && p.GetMethod != null
                    && !typeof(ICommand).IsAssignableFrom(p.PropertyType) && p.PropertyType != typeof(DlssCardViewModel)
                    && p.Name != nameof(GameEditViewModel.SourceGame))
        .ToArray();

    private static string Show(object? value) => value switch
    {
        null => "<null>",
        string s => s,
        IEnumerable e => string.Join("|", e.Cast<object?>().Select(Show)),
        _ => value.ToString() ?? string.Empty,
    };

    private static Dictionary<string, string> Snapshot(GameEditViewModel vm)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in Readable)
        {
            try { values[p.Name] = Show(p.GetValue(vm)); }
            catch (Exception ex) { values[p.Name] = "!" + ex.GetType().Name; }
        }
        return values;
    }

    /// <summary>Another value for <paramref name="property"/>: the other bool, the next enum, a different item from a list the editor offers.</summary>
    private static bool TryOtherValue(GameEditViewModel vm, PropertyInfo property, out object? other)
    {
        other = null;
        object? current = property.GetValue(vm);
        Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (type == typeof(bool)) { other = !(bool)(current ?? false); return true; }
        if (type.IsEnum)
        {
            var values = Enum.GetValues(type).Cast<object>().ToList();
            other = values[(values.IndexOf(current ?? values[0]) + 1) % values.Count];
            return !Equals(other, current);
        }
        if (type == typeof(string))
        {
            string s = (string?)current ?? string.Empty;
            other = int.TryParse(s, out int n) ? (n + 1).ToString()
                : property.Name.Contains("Path") || property.Name.Contains("Directory") ? @"C:\Elsewhere\other.ps1"
                : s + " changed";
            return true;
        }
        if (type == typeof(int)) { other = (int)(current ?? 0) + 1; return true; }
        if (type == typeof(double)) { other = (double)(current ?? 0d) + 1; return true; }

        // An option record: another item from a list the editor offers of that type.
        foreach (var list in Readable.Where(p => typeof(IEnumerable).IsAssignableFrom(p.PropertyType) && p.PropertyType != typeof(string)))
        {
            if (list.GetValue(vm) is not IEnumerable items) continue;
            foreach (var item in items)
            {
                if (item != null && type.IsInstanceOfType(item) && !Equals(item, current)) { other = item; return true; }
            }
        }
        return false;
    }

    [Fact]
    public void EveryValueThePageShows_IsNamedWhenAnyFieldChangesIt() => WpfTestHost.Run(() =>
    {
        var bound = BoundNames();
        var settable = typeof(GameEditViewModel).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod?.IsPublic == true && p.GetMethod != null && p.GetIndexParameters().Length == 0)
            .ToList();
        Assert.NotEmpty(settable);

        var unnamed = new List<string>();
        int changes = 0;
        foreach (var (label, makeGame) in Games())
        {
            foreach (var (property, clear) in settable.SelectMany(p => new[] { (p, false), (p, p.PropertyType == typeof(string)) }).Distinct())
            {
                var vm = NewEditor(makeGame());
                object? other = string.Empty; // a text box emptied
                if (!clear && !TryOtherValue(vm, property, out other)) continue;
                if (clear && Equals(property.GetValue(vm), string.Empty)) continue;

                var before = Snapshot(vm);
                var raised = new List<string?>();
                vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
                try { property.SetValue(vm, other); }
                catch (TargetInvocationException) { continue; }
                changes++;

                Assert.DoesNotContain(raised, n => string.IsNullOrEmpty(n));
                var after = Snapshot(vm);
                foreach (var (name, value) in after)
                {
                    if (before[name] == value || raised.Contains(name) || !bound.Contains(name)) continue;
                    unnamed.Add($"[{label}] setting {property.Name} changed {name} without naming it");
                }
            }
        }

        Assert.True(changes > 40, $"Only {changes} changes were tried; the check is not reaching the form.");
        Assert.True(unnamed.Count == 0, string.Join("\n", unnamed.Distinct()));
    });

    /// <summary>The buttons on the form that change it without opening a dialog or going online, each with what runs first.</summary>
    private static readonly (string Command, object? Parameter, string? Before)[] FormButtons =
    [
        (nameof(GameEditViewModel.ConvertToLocalCommand), null, null),
        (nameof(GameEditViewModel.UndoConvertToLocalCommand), null, nameof(GameEditViewModel.ConvertToLocalCommand)),
        (nameof(GameEditViewModel.EnterAppIdCommand), null, null),
        (nameof(GameEditViewModel.RemoveMatchCommand), null, null),
        (nameof(GameEditViewModel.ToggleProfileSummaryCommand), null, null),
        (nameof(GameEditViewModel.ResetIconCommand), null, null),
        (nameof(GameEditViewModel.ResetCoverCommand), null, null),
        (nameof(GameEditViewModel.UseAutomaticHeroCommand), null, null),
        (nameof(GameEditViewModel.UseAutomaticPosterCommand), null, null),
        (nameof(GameEditViewModel.UseAutomaticIconCommand), null, null),
        (nameof(GameEditViewModel.SelectSectionCommand), "Launch", null),
    ];

    [Fact]
    public void EveryValueThePageShows_IsNamedWhenAButtonChangesIt() => WpfTestHost.Run(() =>
    {
        var bound = BoundNames();
        var unnamed = new List<string>();
        int pressed = 0;
        foreach (var (label, makeGame) in Games())
        {
            foreach (var (command, parameter, before) in FormButtons)
            {
                var vm = NewEditor(makeGame());
                ICommand Get(string name) => (ICommand)typeof(GameEditViewModel).GetProperty(name)!.GetValue(vm)!;
                if (before != null)
                {
                    if (!Get(before).CanExecute(null)) continue;
                    Get(before).Execute(null);
                }
                var button = Get(command);
                if (!button.CanExecute(parameter)) continue;

                var start = Snapshot(vm);
                var raised = new List<string?>();
                vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
                button.Execute(parameter);
                pressed++;

                var after = Snapshot(vm);
                foreach (var (name, value) in after)
                {
                    if (start[name] == value || raised.Contains(name) || raised.Contains(null) || raised.Contains("") || !bound.Contains(name)) continue;
                    unnamed.Add($"[{label}] {command} changed {name} without naming it");
                }
            }
        }

        Assert.True(pressed > 10, $"Only {pressed} buttons were pressed; the check is not reaching the form.");
        Assert.True(unnamed.Count == 0, string.Join("\n", unnamed.Distinct()));
    });
}
