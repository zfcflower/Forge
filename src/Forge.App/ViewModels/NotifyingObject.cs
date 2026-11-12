using System.Runtime.CompilerServices;

namespace Forge.App.ViewModels;

/// <summary>
/// An <see cref="ObservableObject"/> whose setters may also raise the notification for a property derived from the field
/// they set — a <c>HasX</c> flag next to a nullable <c>X</c>, or a formatted line built from a counter. Nothing is
/// overridden and no state is added: the type only holds the setter shape that would otherwise be retyped per property.
/// </summary>
internal abstract class NotifyingObject : ObservableObject
{
    /// <summary>
    /// Assigns <paramref name="field"/> and, when the value actually changed, raises the change notification for
    /// <paramref name="derivedPropertyName"/> as well; returns whether the assignment changed anything. The derived name
    /// is a <see langword="nameof"/> constant at every call site, so the extra notification allocates nothing.
    /// </summary>
    protected bool SetAndNotify<T>(ref T field, T value, string derivedPropertyName, [CallerMemberName] string? propertyName = null)
    {
        if (!Set(ref field, value, propertyName))
        {
            return false;
        }

        OnPropertyChanged(derivedPropertyName);
        return true;
    }
}
