using RAM.Core.Roblox.Rdd;

var service = new RobloxDeploymentService();
string targetRoot = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "ram-rdd-live-test");
Directory.CreateDirectory(targetRoot);

Console.WriteLine($"[1/3] Resolving current WindowsPlayer version...");
string version = await service.ResolveCurrentVersionAsync();
Console.WriteLine($"      -> {version}");

Console.WriteLine($"[2/3] Fetching manifest ({version})...");
var entries = await service.FetchManifestAsync(version);
Console.WriteLine($"      -> {entries.Count} deployment files");
foreach (var e in entries.Take(6))
    Console.WriteLine($"         {e.FileName}  ({e.UncompressedSize/1024.0/1024.0:0.0} MB uncompressed)");

Console.WriteLine($"[3/3] Installing into {targetRoot} (tagged 'Default')...");
// Per-chunk download/extract reports carry an empty message; only print the meaningful ones.
var progress = new Progress<InstallProgress>(m =>
{
    if (!string.IsNullOrWhiteSpace(m.Message))
        Console.WriteLine($"      {m.Message}");
});
string folder = await service.InstallAsync(version, targetRoot, tag: "Default", progress: progress);
Console.WriteLine($"DONE → {folder}");

string exe = Path.Combine(folder, "RobloxPlayerBeta.exe");
string tagFile = Path.Combine(folder, RobloxDeploymentService.TagFileName);
Console.WriteLine($"RobloxPlayerBeta.exe exists: {File.Exists(exe)}");
Console.WriteLine($"tag = {File.ReadAllText(tagFile)}");
