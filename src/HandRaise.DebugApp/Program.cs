using HandRaise.DebugApp;
using HandRaise.Infrastructure.Windows.Hardware;

try
{
    var options = DebugAppOptions.Parse(args);
    var configuration = DebugConfiguration.Load();
    var inventory = options.ListEvents
        ? new HandRaise.Application.Hardware.HardwareInventory(
            [], HandRaise.Application.Hardware.RuntimeInfo.DevelopmentFallback, [])
        : await new WindowsDeviceDetector(TimeSpan.FromSeconds(3)).DetectAsync();
    foreach (var warning in inventory.Warnings)
    {
        Console.Error.WriteLine($"ADVERTENCIA: {warning}");
    }

    using var shutdown = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        shutdown.Cancel();
    };
    return await new DebugRunner(configuration, options, inventory.Devices)
        .RunAsync(shutdown.Token);
}
catch (OperationCanceledException)
{
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
