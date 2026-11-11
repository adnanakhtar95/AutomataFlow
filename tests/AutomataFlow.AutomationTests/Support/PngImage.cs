using System.Buffers.Binary;
using System.IO.Compression;

namespace AutomataFlow.AutomationTests.Support;

/// <summary>Minimal PNG decoder (8-bit RGB/RGBA, non-interlaced) to verify screenshot masking pixel by pixel.</summary>
public sealed class PngImage
{
    private readonly byte[] _pixels;
    private readonly int _bytesPerPixel;

    private PngImage(int width, int height, int bytesPerPixel, byte[] pixels)
    {
        Width = width;
        Height = height;
        _bytesPerPixel = bytesPerPixel;
        _pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public static PngImage Decode(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (png.Length < 8 || png[0] != 0x89 || png[1] != (byte)'P' || png[2] != (byte)'N' || png[3] != (byte)'G')
        {
            throw new InvalidDataException("Not a PNG.");
        }

        int width = 0, height = 0, bytesPerPixel = 0;
        using var idat = new MemoryStream();
        var offset = 8;
        while (offset < png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            var data = png.AsSpan(offset + 8, length);
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                var bitDepth = data[8];
                var colorType = data[9];
                var interlace = data[12];
                if (bitDepth != 8 || interlace != 0 || colorType is not (2 or 6))
                {
                    throw new NotSupportedException($"PNG format not supported (depth {bitDepth}, color {colorType}, interlace {interlace}).");
                }

                bytesPerPixel = colorType == 6 ? 4 : 3;
            }
            else if (type == "IDAT")
            {
                idat.Write(data);
            }
            else if (type == "IEND")
            {
                break;
            }

            offset += 12 + length;
        }

        idat.Position = 0;
        using var inflater = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        inflater.CopyTo(raw);
        var bytes = raw.ToArray();

        var stride = width * bytesPerPixel;
        var pixels = new byte[stride * height];
        var previous = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var filter = bytes[y * (stride + 1)];
            var line = bytes.AsSpan((y * (stride + 1)) + 1, stride);
            var current = pixels.AsSpan(y * stride, stride);
            for (var x = 0; x < stride; x++)
            {
                var a = x >= bytesPerPixel ? current[x - bytesPerPixel] : 0;
                var b = previous[x];
                var c = x >= bytesPerPixel ? previous[x - bytesPerPixel] : 0;
                current[x] = filter switch
                {
                    0 => line[x],
                    1 => (byte)(line[x] + a),
                    2 => (byte)(line[x] + b),
                    3 => (byte)(line[x] + ((a + b) / 2)),
                    4 => (byte)(line[x] + Paeth(a, b, c)),
                    _ => throw new InvalidDataException("Unknown PNG filter."),
                };
            }

            current.CopyTo(previous);
        }

        return new PngImage(width, height, bytesPerPixel, pixels);
    }

    public (byte R, byte G, byte B) Pixel(int x, int y)
    {
        var i = ((y * Width) + x) * _bytesPerPixel;
        return (_pixels[i], _pixels[i + 1], _pixels[i + 2]);
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
