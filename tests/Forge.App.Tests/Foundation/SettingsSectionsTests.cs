using System.Collections.Specialized;
using Forge.App.Localization;
using Forge.App.Preferences;
using Xunit;

namespace Forge.App.Tests.Foundation;

/// <summary>
/// Pins the settings-section registry (forge-app spec, "偏好、文案与设置分区"): features contribute a section instead of
/// editing the settings page, the page shows them by ascending order (stable for equal orders), a duplicate id is a
/// defect rather than a silent second entry, and the heading is a live <see cref="Loc"/> key.
/// </summary>
public sealed class SettingsSectionsTests : IDisposable
{
    private readonly UiLanguage previousLanguage = Loc.Instance.Language;

    public void Dispose() => Loc.Instance.Language = previousLanguage;

    [Fact]
    public void Sections_are_ordered_by_their_order()
    {
        var sections = new SettingsSections();

        sections.Add(Section(SettingsSectionIds.Paths, SettingsSectionIds.PathsOrder));
        sections.Add(Section(SettingsSectionIds.Provider, SettingsSectionIds.ProviderOrder));
        sections.Add(Section(SettingsSectionIds.Appearance, SettingsSectionIds.AppearanceOrder));

        Assert.Equal(
            [SettingsSectionIds.Provider, SettingsSectionIds.Appearance, SettingsSectionIds.Paths],
            sections.Items.Select(static section => section.Id));
    }

    [Fact]
    public void Sections_with_the_same_order_keep_their_insertion_order()
    {
        var sections = new SettingsSections();

        sections.Add(Section("first", 100));
        sections.Add(Section("second", 100));
        sections.Add(Section("third", 100));

        Assert.Equal(["first", "second", "third"], sections.Items.Select(static section => section.Id));
    }

    [Fact]
    public void A_duplicate_id_throws_and_leaves_the_registry_alone()
    {
        var sections = new SettingsSections();
        sections.Add(Section(SettingsSectionIds.Provider, SettingsSectionIds.ProviderOrder));

        Assert.Throws<InvalidOperationException>(
            () => sections.Add(Section(SettingsSectionIds.Provider, SettingsSectionIds.PathsOrder)));

        Assert.Single(sections.Items);
    }

    [Fact]
    public void Adding_a_section_raises_a_collection_change()
    {
        var sections = new SettingsSections();
        var changes = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)sections.Items).CollectionChanged += (_, e) => changes.Add(e.Action);

        sections.Add(Section(SettingsSectionIds.Paths, SettingsSectionIds.PathsOrder));
        sections.Add(Section(SettingsSectionIds.Provider, SettingsSectionIds.ProviderOrder));

        // The low-order section is inserted in front of the existing one, not appended.
        Assert.Equal([NotifyCollectionChangedAction.Add, NotifyCollectionChangedAction.Add], changes);
        Assert.Equal(SettingsSectionIds.Provider, sections.Items[0].Id);
    }

    [Fact]
    public void The_heading_follows_the_language()
    {
        Loc.Instance.Language = UiLanguage.English;
        var section = new SettingsSection(
            SettingsSectionIds.Provider,
            CoreStrings.SettingsSectionProvider,
            SettingsSectionIds.ProviderOrder,
            new object());
        var raised = PropertyChangeLog.For(section);

        Loc.Instance.Language = UiLanguage.Chinese;
        section.RaiseTitle();

        Assert.Equal("模型提供商", section.Title);
        Assert.Equal([nameof(SettingsSection.Title)], raised);
    }

    private static SettingsSection Section(string id, int order) => new(id, CoreStrings.SettingsSectionPaths, order, new object());
}
