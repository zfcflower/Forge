using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Forge.Scripting;

/// <summary>Reads a PE image far enough to tell a managed assembly from native code, so native bytes are never
/// handed to an assembly loader and a verified dependency set can be keyed by the assembly simple name the
/// loader asks for rather than by its file name.</summary>
public static class ManagedImage
{
    /// <summary>True when <paramref name="image"/> carries managed metadata with an assembly manifest, so it can be
    /// loaded from memory. Native libraries, resource-only and non-PE bytes report <c>false</c>.</summary>
    public static bool IsManaged(byte[] image) => ReadAssemblyName(image) is not null;

    /// <summary>The assembly simple name declared in <paramref name="image"/>, or <c>null</c> when the bytes are not a
    /// managed assembly image.</summary>
    public static string? ReadAssemblyName(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Length == 0)
        {
            return null;
        }

        try
        {
            using var peReader = new PEReader(new MemoryStream(image, writable: false));
            if (!peReader.HasMetadata)
            {
                return null;
            }

            var metadata = peReader.GetMetadataReader();
            if (!metadata.IsAssembly)
            {
                return null;
            }

            var name = metadata.GetString(metadata.GetAssemblyDefinition().Name);
            return name.Length == 0 ? null : name;
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }
}
