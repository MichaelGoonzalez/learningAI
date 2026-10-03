using System.Text.Json;
using System.Text.Json.Serialization;
using HandRaise.Application.Hardware;
using HandRaise.Infrastructure.Windows.Hardware;
using HandRaise.Infrastructure.Windows.Settings;

var settingsArgument = Array.FindIndex(
    args,
    argument => string.Equals(argument, "--settings", StringComparison.OrdinalIgnoreCase));
var settingsPath = settingsArgument >= 0 && settingsArgument + 1 < args.Length
    ? Path.GetFullPath(args[settingsArgument + 1])
    : null;

var detector = new WindowsDeviceDetector(TimeSpan.FromSeconds(3));
var inventory = await detector.DetectAsync();
var store = new JsonUserSettingsStore(settingsPath);
var preferences = new DevicePreferenceService(store);
var selection = await preferences.InitializeAsync(inventory.Devices);

var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
};

Console.WriteLine(JsonSerializer.Serialize(new
{
    active_device = selection.Device,
    selection.UsedFallback,
    selection.Warning,
    inventory.Devices,
    inventory.Runtime,
    inventory.Warnings,
    settings_file = store.FilePath
}, jsonOptions));
