using LightweightIDE.App.Application;
using LightweightIDE.DotNet.Build;

// Forget the SDK that may have launched us (dotnet run): every solution must use the SDK its global.json selects.
DotNetEnvironment.CleanCurrentProcess();

var startPath = args.FirstOrDefault(a => !a.StartsWith('-'));
using var host = AppHost.Create();
return host.Run(startPath);
