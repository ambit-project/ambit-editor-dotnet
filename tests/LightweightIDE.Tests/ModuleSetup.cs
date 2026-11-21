using System.Runtime.CompilerServices;
using LightweightIDE.DotNet.Build;

namespace LightweightIDE.Tests;

static class ModuleSetup
{
    /// <summary>Same as the IDE at startup: forget the SDK that launched the test host.</summary>
    [ModuleInitializer]
    internal static void Init() => DotNetEnvironment.CleanCurrentProcess();
}
