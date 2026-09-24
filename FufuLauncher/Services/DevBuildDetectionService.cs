/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/
using System.Diagnostics;
using FufuLauncher.Contracts.Services;
using FufuLauncher.Helpers;
using FufuLauncher.Updates;

namespace FufuLauncher.Services
{
    public class DevBuildDetectionService : IDevBuildDetectionService
    {
        public bool IsDevBuild { get; private set; }

        public bool HasChecked { get; private set; }

        public Task<bool> DetectAsync(string serverVersion)
        {
            IsDevBuild = ReleaseUpdateClient.ReadInstalledBuild(AppContext.BaseDirectory) is null;
            HasChecked = true;

            Debug.WriteLine($"[DevBuildDetection] IsDevBuild={IsDevBuild}, " +
                            $"本地版本={AppVersionHelper.NumericVersion}, 服务器版本={serverVersion}");
            return Task.FromResult(IsDevBuild);
        }
    }
}
