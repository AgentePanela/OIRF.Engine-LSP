namespace OIRF.LanguageServer.Assets;

/// <summary>Reads a PNG's pixel size from its IHDR chunk without decoding the image.</summary>
public static class PngDimensions
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static bool TryRead(string path, out int width, out int height)
    {
        width = 0;
        height = 0;

        try
        {
            // 8-byte signature, 4-byte chunk length, "IHDR", then big-endian width and height.
            Span<byte> header = stackalloc byte[24];
            using var stream = File.OpenRead(path);
            if (stream.Read(header) < header.Length)
                return false;

            if (!header[..8].SequenceEqual(Signature) || !header.Slice(12, 4).SequenceEqual("IHDR"u8))
                return false;

            width = ReadInt32BigEndian(header.Slice(16, 4));
            height = ReadInt32BigEndian(header.Slice(20, 4));
            return width > 0 && height > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static int ReadInt32BigEndian(ReadOnlySpan<byte> bytes) =>
        System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes);
}
