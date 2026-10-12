using System.Collections.Generic;

namespace TrayTrigger.Models;

/// <summary>
/// A named set of library filter ticks (Library toolbar's filter flyout, "Saved"). Only the option
/// keys are stored - the category tab and the search text are not part of it - and a key that no
/// longer matches an option (a launcher that has left the library) is simply skipped on apply.
/// </summary>
public sealed class SavedLibraryFilter
{
    public string Name { get; set; } = string.Empty;
    public List<string> Keys { get; set; } = new();
}
