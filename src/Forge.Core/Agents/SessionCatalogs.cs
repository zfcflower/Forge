using Forge.Abstractions;
using Forge.Core.Storage;

namespace Forge.Core.Agents;

internal static class SessionCatalogs
{
    internal static ISessionCatalog? From(ISessionStore? store) => store switch
    {
        LoggingSessionStore { SupportsCatalog: false } => null,
        ISessionCatalog catalog => catalog,
        _ => null,
    };
}
