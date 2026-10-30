using System.Globalization;
using System.Runtime.CompilerServices;

/// <summary>
/// The golden traces, replays and expected strings were recorded with en-US number formatting ("100%", "0.65"), and
/// Creatura formats numbers in its texts with the current culture. Every test assembly links this file, so the tests
/// run in en-US on any machine (a CI runner's invariant culture writes "100 %"; a German one writes "0,65").
/// </summary>
internal static class TestCulture
{
    [ModuleInitializer]
    internal static void Pin()
    {
        // CREATURA_TEST_CULTURE runs the whole suite as another machine would (e.g. de-DE), to find texts that follow it.
        var culture = CultureInfo.GetCultureInfo(Environment.GetEnvironmentVariable("CREATURA_TEST_CULTURE") ?? "en-US");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}
