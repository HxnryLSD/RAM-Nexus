using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace RAM.Installer;

/// <summary>
/// The installer payload container: every app file stored as an individual Brotli stream
/// with a small header. Brotli was chosen over LZMA/Zip because decompression is fast and
/// needs little memory (window 22 ≈ 4 MB), so installs run fine on low-end machines.
///
/// Layout (all lengths little-endian):
///   "RAMP"         4 bytes magic
///   byte           1 (version)
///   ushort len + utf8 app version
///   int            entry count
///   per entry: ushort len + utf8 relative path ('/' separators), long original size,
///              long compressed size, then the Brotli bytes
/// </summary>
public static class Payload
{
    private const string Magic = "RAMP";
    private const byte Version = 1;

    // WindowsAppSDK ML/AI files that are dead weight for this app (~45 MB) — each batch
    // was verified against a live launch of the trimmed app:
    //   onnxruntime*, DirectML, ML.OnnxRuntime  — ML inference engine, never loaded;
    //   Vision winmds + AI.winmd + AICapabilities — metadata/detection stubs.
    // Keep Microsoft.InteractiveExperiences.Projection.dll — WinUI needs it at startup.
    private static readonly string[] ExcludePrefixes =
    {
        "onnxruntime", "DirectML", "Microsoft.ML.OnnxRuntime",
        "Microsoft.Windows.Vision", "Microsoft.Windows.Internal.Vision",
        "Microsoft.Windows.AI.winmd", "Microsoft.Windows.AI.AICapabilities",
    };

    private static bool ShouldExclude(string fileName) =>
        ExcludePrefixes.Any(p => fileName.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Delete the unused WindowsAppSDK ML/AI files from a directory (used for the portable copy).</summary>
    public static int Trim(string dir)
    {
        int removed = 0;
        foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                     .Where(f => ShouldExclude(Path.GetFileName(f))))
        {
            File.Delete(file);
            removed++;
        }
        Console.WriteLine($"Trimmed {removed} unused files from {dir}");
        return 0;
    }

    /// <summary>Compress <paramref name="srcDir"/> into <paramref name="outFile"/>. Exit code 0 on success.</summary>
    public static int Pack(string srcDir, string outFile)
    {
        var files = Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories)
            .Where(f => !ShouldExclude(Path.GetFileName(f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        using var fs = File.Create(outFile);
        using var bw = new BinaryWriter(fs);
        bw.Write(Encoding.ASCII.GetBytes(Magic));
        bw.Write(Version);

        byte[] appVersion = Encoding.UTF8.GetBytes(ReadAppVersion(srcDir));
        bw.Write((ushort)appVersion.Length);
        bw.Write(appVersion);

        // All entry headers first, then all data in the same order — Extract reads the
        // headers in one pass (to know the total for progress) and the data in a second
        // sequential pass, so no seeking is required even for non-seekable resource streams.
        var payloads = new List<(string Name, long Orig, byte[] Data)>(files.Count);
        foreach (string file in files)
        {
            payloads.Add((Path.GetRelativePath(srcDir, file).Replace('\\', '/'), new FileInfo(file).Length, Compress(file)));
        }

        bw.Write(payloads.Count);
        foreach (var (name, orig, data) in payloads)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            bw.Write((ushort)nameBytes.Length);
            bw.Write(nameBytes);
            bw.Write(orig);
            bw.Write((long)data.Length);
        }

        foreach (var (_, _, data) in payloads)
            bw.Write(data);

        Console.WriteLine($"Packed {payloads.Count} files -> {outFile}");
        return 0;
    }

    /// <summary>Decompress every entry of <paramref name="payload"/> into <paramref name="destDir"/>; returns the app version.</summary>
    public static string Extract(Stream payload, string destDir, IProgress<double>? progress)
    {
        using var br = new BinaryReader(payload, Encoding.UTF8, leaveOpen: false);
        if (Encoding.ASCII.GetString(br.ReadBytes(4)) != Magic)
            throw new InvalidDataException("Not a RAM installer payload.");
        if (br.ReadByte() != Version)
            throw new InvalidDataException("Unsupported payload version.");

        ushort verLen = br.ReadUInt16();
        string appVersion = Encoding.UTF8.GetString(br.ReadBytes(verLen));
        int count = br.ReadInt32();

        var entries = new List<(string Name, long Orig, long Comp)>(count);
        long total = 0;
        for (int i = 0; i < count; i++)
        {
            ushort nameLen = br.ReadUInt16();
            string name = Encoding.UTF8.GetString(br.ReadBytes(nameLen));
            long orig = br.ReadInt64();
            long comp = br.ReadInt64();
            if (comp > int.MaxValue)
                throw new InvalidDataException($"Entry {name} is too large to unpack.");
            entries.Add((name, orig, comp));
            total += comp;
        }

        long done = 0;
        foreach (var (name, orig, comp) in entries)
        {
            string dest = Path.Combine(destDir, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            byte[] buffer = new byte[comp];
            br.BaseStream.ReadExactly(buffer);
            using var ms = new MemoryStream(buffer, writable: false);
            using var brotli = new BrotliStream(ms, CompressionMode.Decompress);
            using var output = File.Create(dest);
            brotli.CopyTo(output);

            done += comp;
            progress?.Report(total == 0 ? 1.0 : (double)done / total);
        }
        return appVersion;
    }

    private static byte[] Compress(string file)
    {
        using var input = File.OpenRead(file);
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            input.CopyTo(brotli);
        return output.ToArray();
    }

    private static string ReadAppVersion(string srcDir)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(Path.Combine(srcDir, "Roblox Account Manager.exe")).FileVersion ?? "1.0.0";
        }
        catch
        {
            return "1.0.0";
        }
    }
}
