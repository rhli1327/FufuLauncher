# Release update checks

```sh
dotnet run --project tests/ReleaseUpdateRegression/ReleaseUpdateRegression.csproj -c Release
```

The tests compile the shared production update client and supply an in-memory
GitHub API and asset server. They cover a new build with the same upstream version,
unchanged builds, older builds, legacy installs without metadata, preview channel
selection, and invalid or missing manifests, hashes, and download URLs.

Changing a version label alone must never trigger or suppress an update.
Both Windows release workflows run these checks before packaging.
