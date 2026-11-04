namespace FufuLauncher.Constants;

// Windows packaging fills these from the exact source-built ZIP and DLL before
// recompiling the application. An incomplete developer build fails closed.
internal static class PluginBuildPins
{
    internal const string BundleUrl = "";
    internal const string BundleSha256 = "";
    internal const string DllSha256 = "";
}
