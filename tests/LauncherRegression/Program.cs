using System.Security.Cryptography;
using FufuLauncher.Services;

// All fixtures live in this test executable's output, never in the application output.
string pluginRelativePath = Path.Combine("Plugins", "FuFuPlugin", "FufuLauncher.UnlockerIsland.dll");
string manifestRelativePath = Path.Combine("Assets", "Launcher", "hash.txt");
string pluginPath = Path.Combine(AppContext.BaseDirectory, pluginRelativePath);
string manifestPath = Path.Combine(AppContext.BaseDirectory, manifestRelativePath);
Directory.CreateDirectory(Path.GetDirectoryName(pluginPath)!);
Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);

if (args.Length == 2 && args[0] == "--packaged")
{
    // Exercise the native core and final integrity manifest produced by the release build.
    string packagedDirectory = Path.GetFullPath(args[1]);
    foreach (string relativePath in new[] { "Launcher.dll", pluginRelativePath, manifestRelativePath })
        File.Copy(Path.Combine(packagedDirectory, relativePath), Path.Combine(AppContext.BaseDirectory, relativePath), true);
}
else if (args.Length == 0)
{
    File.WriteAllText(pluginPath, "Reviewed plugin fixture");
    File.WriteAllLines(manifestPath, ["", "", "", Convert.ToHexString(SHA512.HashData(File.ReadAllBytes(pluginPath)))]);
}
else
{
    throw new ArgumentException("Usage: LauncherRegression [--packaged <application output>]");
}

int passed = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException($"FAIL: {description}");
    Console.WriteLine($"PASS: {description}");
    passed++;
}

var launcher = new LauncherService();
if (args.Length != 0)
    Check(LauncherService.IsLauncherDllLoaded, "Windows can load the packaged native launcher with its final manifest");

// This is the same resolution + validation used by Program.RunElevatedInjection.
// The native GetDefaultDllPath export returns an empty string in upstream 1.7.0.0.
string resolvedPath = launcher.GetDefaultDllPath();
Check(resolvedPath == pluginPath, "Elevated launch resolves the bundled plugin without the native default-path export");
Check(LauncherService.IsAllowedPluginDllPath(resolvedPath), "Elevated launch accepts the packaged plugin and manifest");
Check(!LauncherService.IsAllowedPluginDllPath(""), "Empty plugin paths remain rejected");

string outsidePath = Path.Combine(AppContext.BaseDirectory, "outside.dll");
byte[] originalPlugin = File.ReadAllBytes(pluginPath);
byte[] originalManifest = File.ReadAllBytes(manifestPath);
try
{
    File.Copy(pluginPath, outsidePath, true);
    Check(!LauncherService.IsAllowedPluginDllPath(outsidePath), "Identical bytes outside the allowed plugin path remain rejected");

    File.AppendAllText(pluginPath, "tampered");
    Check(!LauncherService.IsAllowedPluginDllPath(resolvedPath), "A modified plugin at the correct path remains rejected");

    File.WriteAllText(pluginPath, "Untrusted replacement plugin");
    File.WriteAllLines(manifestPath, ["", "", "", new string('0', 128)]);
    Check(!LauncherService.IsAllowedPluginDllPath(resolvedPath), "An untrusted plugin hash remains rejected");

    File.Delete(pluginPath);
    Check(!LauncherService.IsAllowedPluginDllPath(resolvedPath), "A missing plugin remains rejected");
}
finally
{
    File.WriteAllBytes(pluginPath, originalPlugin);
    File.WriteAllBytes(manifestPath, originalManifest);
    File.Delete(outsidePath);
}

Console.WriteLine($"{passed} launcher regression checks passed.");
