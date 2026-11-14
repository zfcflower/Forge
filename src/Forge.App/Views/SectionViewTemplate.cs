using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace Forge.App.Views;

/// <summary>
/// The one data template for settings sections that arrive after <c>App.axaml</c> froze. A wave registers its section view
/// model together with a factory for its view from its own composition module, so no wave edits <c>App.axaml</c>. Explicit
/// factories keep it Native AOT safe (no name-based type lookup). Registration happens on the UI thread at startup.
/// </summary>
internal sealed class SectionViewTemplate : IDataTemplate
{
    private static readonly Dictionary<Type, Func<Control>> Views = [];

    /// <summary>Draws <typeparamref name="TViewModel"/> with the view the factory builds; its DataContext is set to the section.</summary>
    public static void Register<TViewModel>(Func<Control> factory) where TViewModel : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        Views[typeof(TViewModel)] = factory;
    }

    public bool Match(object? data) => data is not null && Views.ContainsKey(data.GetType());

    public Control? Build(object? param) =>
        param is not null && Views.TryGetValue(param.GetType(), out var factory) ? factory() : null;
}
