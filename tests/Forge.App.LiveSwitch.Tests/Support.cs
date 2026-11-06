using System.Runtime.CompilerServices;
using Avalonia.Headless;
using Forge.App.Localization;
using Forge.App.Preferences;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Forge.App.LiveSwitch.Tests;

/// <summary>
/// The one headless session of this assembly: one dispatcher thread for every test, so a window left behind by an
/// earlier test (still rooted by Avalonia statics) can never be notified from a different thread.
/// </summary>
public sealed class HeadlessSessionFixture : IDisposable
{
    public HeadlessSessionFixture() => Session = HeadlessUnitTestSession.StartNew(typeof(App));

    public HeadlessUnitTestSession Session { get; }

    public void Dispose() => Session.Dispose();
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LiveSwitchCollection : ICollectionFixture<HeadlessSessionFixture>
{
    public const string Name = "forge-live-switch";
}

/// <summary>Pins follow-the-system to Chinese before any test runs (the asserted product texts are Chinese).</summary>
internal static class TestLanguageSetup
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        Loc.SystemLanguageOverride = UiLanguage.Chinese;
        Loc.Instance.Language = UiLanguage.System;
    }
}
