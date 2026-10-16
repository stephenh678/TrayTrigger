using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml.Linq;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.Services.Theming;
using TrayTrigger.ViewModels;
using TrayTrigger.Views;
using Xunit;
using static TrayTrigger.Tests.SourceTree;

namespace TrayTrigger.Tests;

/// <summary>
/// The failures that stop TrayTrigger starting and that no other test sees, because the unit tests run without App.xaml: a view that
/// asks for a resource that does not exist (WPF throws when the view loads), a style that does not parse, a converter that is gone.
/// This loads the app's own resources the way the running app has them, builds every window and view that can be built without the
/// running app, and checks that every resource a view names is defined somewhere.
/// </summary>
public class StartupSmokeTests
{
    /// <summary>App.xaml's resources, read from the file and loaded as a dictionary (the Application element itself cannot be created twice).</summary>
    private static ResourceDictionary LoadAppResources()
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var root = XDocument.Parse(Read("App.xaml")).Root!;
        var resources = root.Element(presentation + "Application.Resources")!.Elements().First();

        // The dictionary needs the namespaces the Application element declared; those of this assembly name it, as the compiler does.
        foreach (var declaration in root.Attributes().Where(a => a.IsNamespaceDeclaration))
            if (resources.Attribute(declaration.Name) == null)
                resources.Add(new XAttribute(declaration.Name, declaration.Value));
        string xaml = Regex.Replace(resources.ToString(), @"clr-namespace:(TrayTrigger[\w.]*)""", @"clr-namespace:$1;assembly=TrayTrigger""");

        var context = new ParserContext { BaseUri = new Uri("pack://application:,,,/TrayTrigger;component/App.xaml") };
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xaml));
        return (ResourceDictionary)XamlReader.Load(stream, context);
    }

    /// <summary>
    /// Runs the body with the app's resources in place, and takes them out again after: other tests share this Application, and
    /// some of them rely on it having none. The body runs as one piece on the UI thread, so no other test sees them in between.
    /// </summary>
    private static void WithAppResources(Action<ResourceDictionary> body) => WpfTestHost.Run(() =>
    {
        var app = LoadAppResources();
        Application.Current.Resources.MergedDictionaries.Add(app);
        try { body(app); }
        finally { Application.Current.Resources.MergedDictionaries.Remove(app); }
    });

    private static void Build(List<string> failures, string name, Func<Window?> make)
    {
        try
        {
            var window = make();
            window?.Close();
        }
        catch (Exception ex)
        {
            var cause = ex.InnerException ?? ex;
            failures.Add($"{name}: {cause.GetType().Name}: {cause.Message.Split('\n')[0]}");
        }
    }

    [Fact]
    public void TheAppsResources_LoadWithTheirStylesAndConverters() => WithAppResources(app =>
    {
        Assert.True(app.Count > 30, $"App.xaml loaded only {app.Count} resources.");
        Assert.Equal(3, app.MergedDictionaries.Count);
        foreach (string key in new[] { "AppFont", "IconFont", "MonoFont", "ModernComboBox", "BoolToVis" })
            Assert.True(app.Contains(key), $"App.xaml has no resource named {key}.");
    });

    [Fact]
    public void EveryViewAndWindowThatNeedsNoArguments_LoadsWithTheAppsResources() => WithAppResources(_ =>
    {
        var types = typeof(TrayTrigger.App).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && (typeof(UserControl).IsAssignableFrom(t) || typeof(Window).IsAssignableFrom(t))
                        && t.GetConstructor(Type.EmptyTypes) != null)
            .OrderBy(t => t.Name)
            .ToList();
        Assert.True(types.Count >= 10, $"Only {types.Count} views were found; the scan should find them all.");

        var failures = new List<string>();
        foreach (var type in types)
            Build(failures, type.Name, () =>
            {
                var made = type.GetConstructor(Type.EmptyTypes)!.Invoke(null);
                return made as Window;
            });

        Assert.True(failures.Count == 0, "These did not load:\n" + string.Join('\n', failures));
    });

    [Fact]
    public void TheDialogsThatNeedOnlySimpleArguments_LoadWithTheAppsResources() => WithAppResources(_ =>
    {
        var failures = new List<string>();
        foreach (var icon in Enum.GetValues<DialogIconType>())
            Build(failures, $"ModernDialog ({icon})", () => new ModernDialog("Title", "Message", "Detail", "OK", "Cancel", icon));
        Build(failures, nameof(QuickInputDialog), () => new QuickInputDialog("Title", "Heading", "Prompt", "", null));
        Build(failures, nameof(WelcomeDialog), () => new WelcomeDialog());
        Build(failures, nameof(LauncherDetectionDialog), () => new LauncherDetectionDialog(new List<DetectedLauncherOption>()));
        Build(failures, nameof(GameCandidatePickerDialog), () => new GameCandidatePickerDialog(@"C:\Games", new List<GameCandidate>()));
        Build(failures, nameof(FolderBatchImportDialog),
            () => new FolderBatchImportDialog(@"C:\Games", new List<GameCandidate>(), _ => false, false, _ => { }));
        Build(failures, nameof(ThemeEditDialog), () => new ThemeEditDialog(new ThemeEditorViewModel(ThemeCatalog.BuiltIn("Dark")!, ThemeService.Instance)));

        Assert.True(failures.Count == 0, "These did not load:\n" + string.Join('\n', failures));
    });

    [Fact]
    public void EveryStaticResourceAView_Names_IsDefinedSomewhere()
    {
        // A StaticResource that is not found stops the view from loading, so the window that shows it (or the app, when it is the main
        // window) never opens. A key counts as defined when any XAML file has an x:Key by that name, or code adds one by that name.
        var keys = Sources("*.xaml")
            .SelectMany(f => Regex.Matches(Read(f), @"x:Key=""([^""]+)""").Select(m => m.Groups[1].Value))
            .ToHashSet();
        var code = new Lazy<string>(() => string.Join('\n', Sources("*.cs").Select(Read)));

        var reference = new Regex(@"\{StaticResource\s+([A-Za-z_][\w.]*)\}");
        var missing = new List<string>();
        int total = 0;
        foreach (string file in Sources("*.xaml"))
            foreach (Match m in reference.Matches(Read(file)))
            {
                total++;
                string name = m.Groups[1].Value;
                if (!keys.Contains(name) && !code.Value.Contains("\"" + name + "\"")) missing.Add($"{file}: {name}");
            }

        Assert.True(missing.Count == 0, "Resources named but never defined:\n" + string.Join('\n', missing.Distinct()));
        Assert.True(total > 500, $"The scan should find the views' resource references; found {total}.");
    }
}
