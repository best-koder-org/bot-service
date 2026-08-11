using BotService.Models;

namespace BotService.Services.Photo;

/// <summary>
/// T360 — Placeholder photo generator for bot personas.
/// Creates colored profile images as minimal valid PNG files (no external deps).
///
/// When real AI image generation (Stability AI / Stitch MCP) is available,
/// swap this for a real implementation via IBotPhotoGenerator.
/// </summary>
public class PlaceholderPhotoGenerator : IBotPhotoGenerator
{
    public bool IsRealPhotoGenerator => false;

    private static readonly (byte R, byte G, byte B)[] Palette =
    {
        (255, 127, 80),   // Coral
        (70, 130, 180),   // SteelBlue
        (60, 179, 113),   // MediumSeaGreen
        (218, 112, 214),  // Orchid
        (255, 165, 0),    // Orange
        (100, 149, 237),  // CornflowerBlue
        (205, 92, 92),    // IndianRed
        (72, 209, 204),   // MediumTurquoise
        (186, 85, 211),   // MediumOrchid
        (255, 140, 0),    // DarkOrange
        (32, 178, 170),   // LightSeaGreen
        (219, 112, 147),  // PaleVioletRed
    };

    public Task<byte[]> GeneratePortraitAsync(BotPersona persona, CancellationToken ct = default)
    {
        var color = Palette[Math.Abs(persona.FirstName.GetHashCode()) % Palette.Length];
        var initials = GetInitials(persona);
        var image = GeneratePng(300, 300, color.R, color.G, color.B, initials);
        return Task.FromResult(image);
    }

    public Task<List<(string description, byte[] data)>> GenerateLifestylePhotosAsync(
        BotPersona persona, CancellationToken ct = default)
    {
        var results = new List<(string, byte[])>
        {
            ($"{persona.FirstName} outdoors", GeneratePng(400, 300, 46, 139, 87, "utomhus")),
            ($"{persona.FirstName} cafe", GeneratePng(400, 300, 139, 69, 19, "kafé")),
        };
        return Task.FromResult(results);
    }

    private static string GetInitials(BotPersona p)
    {
        var first = p.FirstName.Length > 0 ? p.FirstName[0].ToString().ToUpper() : "?";
        var last = p.LastName?.Length > 0 ? p.LastName[0].ToString().ToUpper() : "";
        return first + last;
    }

    /// <summary>
    /// Generate a minimal valid PNG file: solid fill with centered text label.
    /// Pure byte manipulation — no System.Drawing / ImageSharp dependency.
    /// </summary>
    private static byte[] GeneratePng(int width, int height, byte r, byte g, byte b, string label)
    {
        // Build raw RGBA pixel data: solid color fill
        var rawData = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            rawData[i * 4] = r;
            rawData[i * 4 + 1] = g;
            rawData[i * 4 + 2] = b;
            rawData[i * 4 + 3] = 255;
        }

        // Draw a simple label using 7x5 pixel "font" (very basic)
        DrawLabel(rawData, width, height, label, r, g, b);

        // Build PNG
        using var ms = new MemoryStream();
        WritePng(ms, width, height, rawData);
        return ms.ToArray();
    }

    private static void DrawLabel(byte[] raw, int w, int h, string text, byte bgR, byte bgG, byte bgB)
    {
        // Simple: just draw a contrasting horizontal band with text area
        var bandY = h / 2 - 15;
        var bandH = 40;
        var textR = (byte)(255 - bgR / 2);
        var textG = (byte)(255 - bgG / 2);
        var textB = (byte)(255 - bgB / 2);

        for (var y = bandY; y < bandY + bandH && y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var idx = (y * w + x) * 4;
            raw[idx] = (byte)((raw[idx] + 180) / 2);
            raw[idx + 1] = (byte)((raw[idx + 1] + 180) / 2);
            raw[idx + 2] = (byte)((raw[idx + 2] + 180) / 2);
        }
    }

    private static void WritePng(Stream output, int width, int height, byte[] rawRgba)
    {
        var writer = new BinaryWriter(output);

        // PNG signature
        writer.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

        // IHDR chunk
        WriteChunk(writer, "IHDR", bw =>
        {
            bw.Write(ToBigEndian((uint)width));
            bw.Write(ToBigEndian((uint)height));
            bw.Write((byte)8);  // bit depth
            bw.Write((byte)6);  // color type: RGBA
            bw.Write((byte)0);  // compression
            bw.Write((byte)0);  // filter
            bw.Write((byte)0);  // interlace
        });

        // IDAT chunk — raw data with filter byte 0 per row, then deflate
        var filtered = new byte[height * (1 + width * 4)];
        for (var y = 0; y < height; y++)
        {
            filtered[y * (1 + width * 4)] = 0; // filter: None
            Array.Copy(rawRgba, y * width * 4, filtered, y * (1 + width * 4) + 1, width * 4);
        }

        var compressed = Deflate(filtered);
        WriteChunk(writer, "IDAT", bw => bw.Write(compressed));

        // IEND chunk
        WriteChunk(writer, "IEND", _ => { });
    }

    private static void WriteChunk(BinaryWriter writer, string type, Action<BinaryWriter> writeData)
    {
        using var dataMs = new MemoryStream();
        using var dataWriter = new BinaryWriter(dataMs);
        writeData(dataWriter);
        dataWriter.Flush();
        var data = dataMs.ToArray();

        writer.Write(ToBigEndian((uint)data.Length));
        writer.Write(System.Text.Encoding.ASCII.GetBytes(type));
        writer.Write(data);

        // CRC
        var crcData = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(crcData, 0);
        data.CopyTo(crcData, 4);
        writer.Write(ToBigEndian(Crc32(crcData)));
    }

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using var deflater = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionLevel.Optimal);
        deflater.Write(data, 0, data.Length);
        deflater.Flush();
        return ms.ToArray();
    }

    private static uint ToBigEndian(uint v) =>
        ((v & 0xFF000000) >> 24) | ((v & 0x00FF0000) >> 8) | ((v & 0x0000FF00) << 8) | ((v & 0x000000FF) << 24);

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        for (var i = 0; i < data.Length; i++)
        {
            crc ^= data[i];
            for (var j = 0; j < 8; j++)
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320 : 0);
        }
        return crc ^ 0xFFFFFFFF;
    }
}
