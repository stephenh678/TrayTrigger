using System.Text.Json;
using TrayTrigger.Models;

namespace TrayTrigger.Tests;

/// <summary>
/// MetadataRefreshInterval is the one enum persisted by name. The converter must never throw on
/// a value it doesn't know: a throw makes StorageService archive settings.json as corrupt and
/// reset every setting, which is far worse than losing this one preference.
/// </summary>
public class MetadataRefreshIntervalConverterTests
{
    private static AppSettings Load(string json) =>
        JsonSerializer.Deserialize(json, AppJsonContext.Default.AppSettings)!;

    [Theory]
    [InlineData("\"Weekly\"", MetadataRefreshInterval.Weekly)]
    [InlineData("\"weekly\"", MetadataRefreshInterval.Weekly)] // case-insensitive, like JsonStringEnumConverter
    [InlineData("\"Never\"", MetadataRefreshInterval.Never)]
    [InlineData("5", MetadataRefreshInterval.Never)] // the number form every other enum uses
    public void Read_AcceptsKnownNamesAndNumbers(string value, MetadataRefreshInterval expected)
    {
        Assert.Equal(expected, Load($$"""{"MetadataRefreshInterval": {{value}}}""").MetadataRefreshInterval);
    }

    [Theory]
    [InlineData("\"Hourly\"")] // added by a newer build
    [InlineData("\"\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("true")]
    public void Read_FallsBackToDefaultInsteadOfThrowing(string value)
    {
        var settings = Load($$"""{"MetadataRefreshInterval": {{value}}, "StartMinimizedToTray": false}""");
        Assert.Equal(MetadataRefreshIntervalJsonConverter.Fallback, settings.MetadataRefreshInterval);
        Assert.Equal(new AppSettings().MetadataRefreshInterval, settings.MetadataRefreshInterval);
        // The rest of the file still loads.
        Assert.False(settings.StartMinimizedToTray);
    }

    [Fact]
    public void Write_PersistsTheName()
    {
        var settings = new AppSettings { MetadataRefreshInterval = MetadataRefreshInterval.Monthly };
        string json = JsonSerializer.Serialize(settings, AppJsonContext.Default.AppSettings);
        Assert.Contains("\"MetadataRefreshInterval\": \"Monthly\"", json);
        Assert.Equal(MetadataRefreshInterval.Monthly, Load(json).MetadataRefreshInterval);
    }
}
