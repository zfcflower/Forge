using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Forge.Scripting;

/// <summary>Reads an image far enough to tell a managed assembly from native code, so native bytes are never
/// handed to an assembly loader and a verified dependency set can be keyed by the assembly simple name the
/// loader asks for rather than by its file name. Native binaries are also recognised by their header signature,
/// whatever the file extension.</summary>
public static class ManagedImage
{
    /// <summary>True when <paramref name="image"/> carries managed metadata with an assembly manifest, so it can be
    /// loaded from memory. Native libraries, resource-only and non-PE bytes report <c>false</c>.</summary>
    public static bool IsManaged(byte[] image) => ReadAssemblyName(image) is not null;

    /// <summary>The native binary format of <paramref name="image"/> — <c>ELF</c>, <c>Mach-O</c>, <c>Mach-O universal</c>
    /// or <c>PE without a CLI header</c> — or <c>null</c> when the bytes are not a native binary image. Only header
    /// bytes are read, so the file extension never decides the answer.</summary>
    public static string? ReadNativeBinaryFormat(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Length >= 4 &&
            image[0] == 0x7F && image[1] == (byte)'E' && image[2] == (byte)'L' && image[3] == (byte)'F')
        {
            return "ELF";
        }

        if (ReadMachOFormat(image) is { } machO)
        {
            return machO;
        }

        return image.Length >= 2 && image[0] == (byte)'M' && image[1] == (byte)'Z' && !IsManaged(image)
            ? "PE without a CLI header"
            : null;
    }

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

    /// <summary>Mach-O magics as read big-endian from the first four bytes: 32-bit, 64-bit, fat, fat 64, and the
    /// byte-swapped (little-endian) encodings of the same four.</summary>
    private static string? ReadMachOFormat(byte[] image) =>
        image.Length < 4
            ? null
            : BinaryPrimitives.ReadUInt32BigEndian(image) switch
            {
                0xFEED_FACE or 0xFEED_FACF or 0xCEFA_EDFE or 0xCFFA_EDFE => "Mach-O",
                0xCAFE_BABE or 0xCAFE_BABF or 0xBEBA_FECA or 0xBFBA_FECA => "Mach-O universal",
                _ => null,
            };
}
