# Launcher regression checks

Run the plugin path and integrity checks on any .NET 8 host:

```sh
dotnet run --project tests/LauncherRegression/LauncherRegression.csproj -c Release
```

The tests compile the production `LauncherService` directly. They cover the
resolution and validation used by the elevated launch process, including a
missing plugin, a modified plugin, an invalid manifest, and an identical DLL
outside the allowed directory. The default-path check fails with the 1.7.0.0
implementation, which depended on an empty native default-path export.

On Windows, test the final native core, plugin, and manifest before packaging:

```powershell
dotnet run --project tests/LauncherRegression/LauncherRegression.csproj -c Release -- --packaged "<application output directory>"
```

Both release workflows run this mode after compression and manifest generation.
It also requires that the native core loads successfully. Fixtures are copied to
the test output directory; the application output remains unchanged. These checks
do not start the game or perform injection.
