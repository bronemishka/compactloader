using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Usage: IconGenerator <drawing.xaml> <output.ico>");
        using var input = File.OpenRead(args[0]);
        var drawing = (DrawingImage)XamlReader.Load(input);
        var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        var frames = new List<byte[]>();
        foreach (var size in sizes)
        {
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen()) context.DrawImage(drawing, new Rect(0, 0, size, size));
            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream(); encoder.Save(stream); frames.Add(stream.ToArray());
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
        using var writer = new BinaryWriter(File.Create(args[1]));
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
        int offset = 6 + sizes.Length * 16;
        for (var i = 0; i < sizes.Length; i++)
        {
            writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i])); writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
            writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write(frames[i].Length); writer.Write(offset); offset += frames[i].Length;
        }
        foreach (var frame in frames) writer.Write(frame);
        File.WriteAllBytes(Path.ChangeExtension(args[1], ".png"), frames[^1]);
        Console.WriteLine($"Created {args[1]} ({sizes.Length} sizes, 16–256 px).");
    }
}
