using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using TrayTrigger.Services.Theming;

namespace TrayTrigger.ViewModels;

/// <summary>
/// One kind of corner in the Customize list (tags, controls, cards or posters): the shape and the size it has. Until the
/// person changes one, it follows the shape and the roundness chosen above them; a poster follows the card, less 1 pixel.
/// Numbers a theme file holds that no shape draws are kept exactly as they are until this row is changed.
/// </summary>
public sealed class CornerRow : ViewModelBase
{
    private readonly ThemeCornersEditor _owner;
    private readonly bool _isPoster;
    private CornerShape _selected;
    private double? _stepOverride;
    private CornerRadius? _custom;
    private bool _byHand;

    internal CornerRow(ThemeCornersEditor owner, CornerTarget target, string sameLabel)
    {
        _owner = owner;
        Target = target;
        _isPoster = target.Key == "RadiusPoster";
        Same = ThemeCorners.SameAs(sameLabel);
        _selected = Same;
        ResetCommand = new RelayCommand(() =>
        {
            Clear();
            _owner.RowChanged();
        });
        Choices = [Same, .. ThemeCorners.Shapes];
    }

    internal CornerTarget Target { get; }
    private CornerShape Same { get; }

    public string Label => Target.Label;

    /// <summary>"Default" (or "Default (follows cards)") first, then the shapes, then "Custom" when the file holds numbers no shape draws.</summary>
    public IReadOnlyList<CornerShape> Choices { get; private set; }

    public CornerShape SelectedShape
    {
        get => _selected;
        set
        {
            if (value == null || ReferenceEquals(value, _selected)) return;
            _selected = value;
            if (!value.IsCustom) _custom = null;
            _byHand = value.IsCustom || !(value.IsSame && _stepOverride == null);
            _owner.RowChanged();
        }
    }

    /// <summary>The size, 0 to 16: the row's own once it was moved, otherwise the one it follows.</summary>
    public double Step
    {
        get => _stepOverride ?? (_isPoster ? _owner.Cards.Step : _owner.Roundness);
        set
        {
            value = Math.Clamp(Math.Round(value), 0, ThemeCorners.MaxStep);
            if (value == Step) return;
            _stepOverride = value;
            _byHand = true;
            _owner.RowChanged();
        }
    }

    public string StepText => Step.ToString("0.#");

    /// <summary>True past the usual range of the slider: the number is shown in the warning color.</summary>
    public bool IsExtreme => Step > ThemeCorners.NormalSteps;

    /// <summary>Puts just this row back to following the choice above it.</summary>
    public ICommand ResetCommand { get; }

    /// <summary>True when this row has its own shape or size (its reset arrow shows, and it is not drawn dim).</summary>
    public bool HasOwnSetting => IsOverridden;

    /// <summary>False for a shape whose roundness does not depend on the size (a pill), and for numbers kept as they are in the file.</summary>
    public bool SizeEnabled => _custom == null && EffectiveShape is { IsFull: false };

    /// <summary>The shape drawn now: the one picked, or the one this row follows. Null for numbers kept from the file.</summary>
    internal CornerShape? EffectiveShape
    {
        get
        {
            if (_custom != null) return null;
            if (!_selected.IsSame) return _selected;
            return _isPoster ? _owner.Cards.EffectiveShape ?? _owner.Shape : _owner.Shape;
        }
    }

    /// <summary>The four corner radii this row works out to.</summary>
    internal CornerRadius Value
    {
        get
        {
            if (_custom is { } kept) return kept;
            // A poster nobody set follows the card, less the card's 1 px border, so the art meets the outline.
            if (_isPoster && !_byHand) return ThemeShape.Inset(_owner.Cards.Value, 1);
            return (EffectiveShape ?? _owner.Shape).At(Target.Px(Step), Target.Max, Target.FullPx);
        }
    }

    /// <summary>True when this row still takes its shape from the choice above it.</summary>
    internal bool FollowsShape => _custom == null && _selected.IsSame;

    /// <summary>True when this row still takes its size from the choice above it.</summary>
    internal bool FollowsSize => _custom == null && _stepOverride == null;

    /// <summary>True when this row does not just follow the choice above it.</summary>
    internal bool IsOverridden => _custom != null || _byHand || !_selected.IsSame || _stepOverride != null;

    /// <summary>Shows in the list as it is now, after anything it follows has changed.</summary>
    internal void Refresh()
    {
        OnPropertyChanged(nameof(SelectedShape));
        OnPropertyChanged(nameof(Step));
        OnPropertyChanged(nameof(StepText));
        OnPropertyChanged(nameof(IsExtreme));
        OnPropertyChanged(nameof(HasOwnSetting));
        OnPropertyChanged(nameof(SizeEnabled));
    }

    internal void Clear()
    {
        _selected = Same;
        _stepOverride = null;
        _custom = null;
        _byHand = false;
    }

    /// <summary>
    /// Gives this row its own shape and/or size (null keeps following the choice above) without telling the owner: for Generate,
    /// which tells it once when all are set.
    /// </summary>
    internal void Pick(CornerShape? shape, double? step)
    {
        _selected = shape == null ? Same : Choices.First(c => c == shape);
        _custom = null;
        _stepOverride = step;
        _byHand = shape != null || step != null;
    }

    internal CornerRowState Capture() => new(_selected, _stepOverride, _custom, _byHand);

    internal void Restore(CornerRowState state) =>
        (_selected, _stepOverride, _custom, _byHand) = (state.Selected, state.StepOverride, state.Custom, state.ByHand);

    /// <summary>Takes the numbers a theme file holds for this kind of corner: following the choice above where they match it, otherwise set by hand.</summary>
    internal void Load(CornerRadius value)
    {
        if (ThemeCorners.Same(value, Value)) return;
        if (ThemeCorners.Recognise(Target, value) is not { } found)
        {
            Keep(value);
            return;
        }
        if (found.Shape != _owner.Shape && !(_isPoster && found.Shape == _owner.Cards.EffectiveShape)) _selected = Choices.First(c => c == found.Shape);
        if (!found.Shape.IsFull && found.Step != Step) _stepOverride = found.Step;
        _byHand = IsOverridden;
    }

    /// <summary>Keeps numbers no shape draws, shown as "Custom", until the person picks a shape for this row.</summary>
    internal void Keep(CornerRadius value)
    {
        var custom = ThemeCorners.Custom();
        Choices = [.. Choices, custom];
        OnPropertyChanged(nameof(Choices));
        _selected = custom;
        _custom = value;
        _byHand = true;
    }
}

/// <summary>What a row held: for putting it back after Generate.</summary>
internal readonly record struct CornerRowState(CornerShape Selected, double? StepOverride, CornerRadius? Custom, bool ByHand);

/// <summary>What the Corners card held: the shape and roundness above, and each row under Customize.</summary>
internal sealed record CornersState(CornerShape Shape, double Roundness, CornerRowState[] Rows);

/// <summary>
/// The Corners card of the Edit Theme window: a shape and a roundness for the whole theme, and, under Customize, a shape and
/// a size for each of tags, controls, cards and posters. What it holds is turned into plain radii (<see cref="Values"/>); the
/// theme file keeps only those numbers, so a file written by hand is shown as the nearest thing it can say, and kept exactly
/// until the person changes that kind of corner.
/// </summary>
public sealed class ThemeCornersEditor : ViewModelBase
{
    private readonly IReadOnlyDictionary<string, CornerRadius>? _file;
    private readonly Dictionary<string, CornerRadius> _defaults;
    private CornerShape _shape;
    private double _roundness = ThemeCorners.DefaultStep;
    private bool _showCustomize;
    private readonly string _initial;

    internal ThemeCornersEditor(ThemeDefinition source)
    {
        _file = source.Shape;
        var app = ThemeShape.Build(null);
        _defaults = ThemeCorners.Targets.ToDictionary(t => t.Key, t => (CornerRadius)app[t.Key]);
        _shape = ThemeCorners.Shapes[0];

        Tags = new CornerRow(this, ThemeCorners.Targets[0], "Default");
        Controls = new CornerRow(this, ThemeCorners.Targets[1], "Default");
        Cards = new CornerRow(this, ThemeCorners.Targets[2], "Default");
        Posters = new CornerRow(this, ThemeCorners.Targets[3], "Default (follows cards)");
        Rows = [Tags, Controls, Cards, Posters];

        // What the theme says now (what it leaves out is the app's own), worked out as the app will.
        var now = ThemeShape.Build(_file);
        var card = (CornerRadius)now["RadiusCard"];
        // Cards are a row like the others: the choice above is the app's own look until a row says otherwise, and a row a
        // theme file sets keeps its own shape and size, so moving the choice above never moves it.
        Cards.Load(card);
        Tags.Load((CornerRadius)now["RadiusSmall"]);
        Controls.Load((CornerRadius)now["RadiusControl"]);
        if (_file?.ContainsKey("RadiusPoster") == true) Posters.Load((CornerRadius)now["RadiusPoster"]);

        // Open at first: the four rows are the card's main content, and a row that follows the choice above is drawn quiet.
        _showCustomize = true;
        ResetCommand = new RelayCommand(Reset);
        ToggleCustomizeCommand = new RelayCommand(() => { ShowCustomize = !ShowCustomize; });
        _initial = Snapshot();
    }

    public CornerRow Tags { get; }
    public CornerRow Controls { get; }
    public CornerRow Cards { get; }
    public CornerRow Posters { get; }
    public IReadOnlyList<CornerRow> Rows { get; }

    public IReadOnlyList<CornerShape> Shapes => ThemeCorners.Shapes;
    public ICommand ResetCommand { get; }
    public ICommand ToggleCustomizeCommand { get; }

    /// <summary>Raised when any corner changed, for the preview to repaint.</summary>
    internal event EventHandler? Changed;

    /// <summary>The shape the whole theme has, unless a row under Customize picks its own.</summary>
    public CornerShape Shape
    {
        get => _shape;
        set
        {
            if (value == null || value == _shape) return;
            _shape = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SizeEnabled));
            RowChanged();
        }
    }

    /// <summary>How round, 0 to 16, for every row that has not been moved by hand. 4 is the app's own look.</summary>
    public double Roundness
    {
        get => _roundness;
        set
        {
            value = Math.Clamp(Math.Round(value), 0, ThemeCorners.MaxStep);
            if (value == _roundness) return;
            _roundness = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RoundnessText));
            OnPropertyChanged(nameof(IsExtreme));
            RowChanged();
        }
    }

    public string RoundnessText => _roundness.ToString("0.#");

    /// <summary>True when the roundness is past the usual range: the window says it may clip text and art.</summary>
    public bool IsExtreme => _roundness > ThemeCorners.NormalSteps;
    /// <summary>True when the default or any row shows its number in the warning color.</summary>
    public bool AnyExtreme => IsExtreme || Rows.Any(r => r.IsExtreme);
    public bool SizeEnabled => !_shape.IsFull;

    /// <summary>False when tags, controls and cards all have their own shape: nothing follows the shape above, so it is greyed out rather than moved to no effect.</summary>
    public bool DefaultUsed => Rows.Any(r => r.Target.Key != "RadiusPoster" && r.FollowsShape);

    /// <summary>The same for the size: some row still follows it, and the shape above has a size.</summary>
    public bool RoundnessEnabled => SizeEnabled && Rows.Any(r => r.Target.Key != "RadiusPoster" && r.FollowsSize);

    public bool ShowCustomize
    {
        get => _showCustomize;
        set
        {
            if (!SetProperty(ref _showCustomize, value)) return;
            OnPropertyChanged(nameof(CustomizeText));
        }
    }

    public string CustomizeText => _showCustomize ? "Hide" : "Customize...";

    /// <summary>True when some kind of corner is not the app's own, which is when Reset has something to do.</summary>
    public bool DiffersFromDefault => ThemeCorners.Targets.Any(t => !ThemeCorners.Same(Row(t.Key).Value, _defaults[t.Key]));

    /// <summary>True when a row under Customize has its own shape or size, which the line under the card says.</summary>
    public bool HasOverrides => Rows.Any(r => r.IsOverridden);

    internal string Snapshot() => string.Join('|', Rows.Select(r =>
        FormattableString.Invariant($"{r.Value.TopLeft},{r.Value.TopRight},{r.Value.BottomRight},{r.Value.BottomLeft}")));

    internal bool HasChanges => Snapshot() != _initial;

    /// <summary>The four radii as the theme would have them: for the preview, and for the file.</summary>
    internal IReadOnlyDictionary<string, CornerRadius> Values => Rows.ToDictionary(r => r.Target.Key, r => r.Value);

    private CornerRow Row(string key) => Rows.First(r => r.Target.Key == key);

    internal void RowChanged()
    {
        // The cards first: the poster follows them.
        foreach (var row in Rows) row.Refresh();
        OnPropertyChanged(nameof(DiffersFromDefault));
        OnPropertyChanged(nameof(HasOverrides));
        OnPropertyChanged(nameof(DefaultUsed));
        OnPropertyChanged(nameof(RoundnessEnabled));
        OnPropertyChanged(nameof(AnyExtreme));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Reset()
    {
        _shape = ThemeCorners.Shapes[0];
        _roundness = ThemeCorners.DefaultStep;
        foreach (var row in Rows) row.Clear();
        NotifyAll();
    }

    /// <summary>What the card holds now, for <see cref="Restore"/>.</summary>
    internal CornersState Capture() => new(_shape, _roundness, Rows.Select(r => r.Capture()).ToArray());

    internal void Restore(CornersState state)
    {
        _shape = state.Shape;
        _roundness = state.Roundness;
        for (int i = 0; i < Rows.Count; i++) Rows[i].Restore(state.Rows[i]);
        NotifyAll();
    }

    /// <summary>
    /// A shape and a roundness for the whole theme, and then, for each kind of corner (tags, controls, cards, posters), usually a shape
    /// and a size of its own, so the kinds differ. Anything on the sliders can come up, any of the eleven shapes and any size from 0
    /// to 16, extreme included (the roundness line says so, and a roll can be undone). Every one of them is still changed by hand like
    /// any other choice.
    /// </summary>
    internal void Randomize(Random random)
    {
        CornerShape AnyShape() => ThemeCorners.Shapes[random.Next(ThemeCorners.Shapes.Count)];
        // Mostly the usual range, and now and then all the way up.
        int AnyStep() => random.NextDouble() < 0.6 ? random.Next(0, (int)ThemeCorners.NormalSteps + 1) : random.Next((int)ThemeCorners.NormalSteps + 1, (int)ThemeCorners.MaxStep + 1);
        bool Sometimes(double chance) => random.NextDouble() < chance;

        _shape = AnyShape();
        _roundness = AnyStep();
        foreach (var row in Rows) row.Clear();
        foreach (var row in Rows)
            row.Pick(Sometimes(0.6) ? AnyShape() : null, Sometimes(0.6) ? AnyStep() : null);
        NotifyAll();
    }

    private void NotifyAll()
    {
        OnPropertyChanged(nameof(Shape));
        OnPropertyChanged(nameof(Roundness));
        OnPropertyChanged(nameof(RoundnessText));
        OnPropertyChanged(nameof(IsExtreme));
        OnPropertyChanged(nameof(SizeEnabled));
        RowChanged();
    }

    /// <summary>
    /// What to write to a theme file that already says <see cref="_file"/>: the radii that are not the app's own and are not
    /// already in the file as they are, and the lines the file has that should go (a radius put back to the app's own, a poster
    /// that follows the card again). A radius that is the same as the file's is left alone, so saving a theme changes only what was changed.
    /// </summary>
    internal (Dictionary<string, CornerRadius> Set, List<string> Clear) ForSave(bool newFile = false)
    {
        var set = new Dictionary<string, CornerRadius>(StringComparer.Ordinal);
        var clear = new List<string>();
        foreach (var row in Rows)
        {
            string key = row.Target.Key;
            var value = row.Value;
            // A copy of a built-in theme is a new file that holds none of the source's corners, so every one that is not the app's own is written.
            bool inFile = !newFile && _file != null && _file.TryGetValue(key, out _);
            CornerRadius inTheFile = inFile ? _file![key] : default;

            if (inFile && ThemeCorners.Same(inTheFile, value)) continue;

            bool explicitValue = row.Target.Key == "RadiusPoster"
                ? row.IsOverridden && !ThemeCorners.Same(value, ThemeShape.Inset(Cards.Value, 1))
                : !ThemeCorners.Same(value, _defaults[key]);
            if (explicitValue) set[key] = value;
            else if (inFile) clear.Add(key);
        }
        return (set, clear);
    }
}
