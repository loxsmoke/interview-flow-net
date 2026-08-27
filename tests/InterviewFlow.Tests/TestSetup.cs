using System.Globalization;
using System.Runtime.CompilerServices;
using InterviewFlow.Core.Logging;

// Config saving writes PROCESS environment variables (so edits apply without a
// restart), and the Loc/telemetry facades are process-wide too — parallel
// collections would race on that shared state.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace InterviewFlow.Tests;

/// <summary>
/// Assembly-wide environment setup (openlogi-net convention): suppress the real
/// diagnostic log file and pin the culture so number/date formatting in assertions
/// is deterministic on any machine.
/// </summary>
internal static class TestSetup
{
    [ModuleInitializer]
    internal static void Init()
    {
        DiagnosticLog.Suppressed = true;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");
    }
}
