using Rafferty.Core;
using Rafferty.Service;
using Rafferty.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (args.Length == 1 && args[0] is "install" or "uninstall")
{
    return await ServiceInstaller.RunAsync(args[0]);
}

Directory.CreateDirectory(AppPaths.MachineDataRoot);
Directory.CreateDirectory(AppPaths.ListsDirectory);
Directory.CreateDirectory(AppPaths.LogsDirectory);
ServiceFiles.EnsureInstalledDefaults();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = AppPaths.ServiceName);
builder.Services.AddSingleton(new RotatingFileLogger(AppPaths.LogsDirectory, "service"));
builder.Services.AddSingleton(new StrategyStore(AppPaths.StrategiesFile, nextGenPath: AppPaths.NextGenStrategiesFile));
builder.Services.AddSingleton<ConnectivityTester>();
builder.Services.AddSingleton<BypassEngineManager>();
builder.Services.AddSingleton<IBypassEngine>(services => services.GetRequiredService<BypassEngineManager>());
builder.Services.AddSingleton<OptimizationEngine>();
builder.Services.AddSingleton<CommandDispatcher>();
builder.Services.AddHostedService<PipeServerWorker>();
builder.Services.AddHostedService<RecoveryWorker>();

await builder.Build().RunAsync();
return 0;

