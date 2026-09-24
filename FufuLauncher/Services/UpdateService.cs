/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/
using System.Diagnostics;
using FufuLauncher.Contracts.Services;
using FufuLauncher.Helpers;
using FufuLauncher.Updates;

namespace FufuLauncher.Services;

public class UpdateService : IUpdateService
{
    private readonly ILocalSettingsService _localSettingsService;
    private readonly IDevBuildDetectionService _devBuildDetectionService;
    private readonly ReleaseUpdateClient _releaseClient;

    public UpdateService(ILocalSettingsService localSettingsService, IDevBuildDetectionService devBuildDetectionService)
    {
        _localSettingsService = localSettingsService;
        _devBuildDetectionService = devBuildDetectionService;
        var httpClient = new HttpClient(new HttpClientHandler { UseCookies = false, MaxAutomaticRedirections = 5 })
        {
            Timeout = TimeSpan.FromSeconds(30),
            DefaultRequestHeaders =
            {
                UserAgent = { new System.Net.Http.Headers.ProductInfoHeaderValue("Fufu-Launcher", AppVersionHelper.NumericVersion) },
                Accept = { new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json") }
            }
        };
        _releaseClient = new ReleaseUpdateClient(httpClient);
    }

    public async Task<UpdateCheckResult> CheckUpdateAsync()
    {
        var isDevBuild = await _devBuildDetectionService.DetectAsync(AppVersionHelper.NumericVersion);
        try
        {
            var installed = ReleaseUpdateClient.ReadInstalledBuild(AppContext.BaseDirectory);
            var stable = await _releaseClient.GetLatestAsync();
            if (stable != null && ReleaseUpdateClient.HasUpdate(installed, stable.Build) &&
                await ShouldAnnounceAsync(stable, "LastAnnouncedStableBuild"))
                return ToResult(stable, false, isDevBuild);

            var previewSetting = await _localSettingsService.ReadSettingAsync("IsPreviewUpdateAnnouncementEnabled");
            if (previewSetting == null || Convert.ToBoolean(previewSetting))
            {
                var preview = await _releaseClient.GetLatestAsync(preview: true);
                if (preview != null && (stable == null || preview.Build.BuiltAtUtc > stable.Build.BuiltAtUtc) &&
                    ReleaseUpdateClient.HasUpdate(installed, preview.Build) &&
                    await ShouldAnnounceAsync(preview, "LastAnnouncedPreviewBuild"))
                    return ToResult(preview, true, isDevBuild);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateService] 构建更新检查失败: {ex}");
        }
        return new UpdateCheckResult { ShouldShowUpdate = false, IsDevBuild = isDevBuild };
    }

    private async Task<bool> ShouldAnnounceAsync(PublishedBuild release, string key)
    {
        var previous = await _localSettingsService.ReadSettingAsync(key);
        if (previous?.ToString() == release.Build.Identity) return false;
        await _localSettingsService.SaveSettingAsync(key, release.Build.Identity);
        return true;
    }

    private static UpdateCheckResult ToResult(PublishedBuild release, bool preview, bool devBuild) => new()
    {
        ShouldShowUpdate = true,
        IsPreview = preview,
        IsDevBuild = devBuild,
        ServerVersion = release.VersionLabel,
        UpdateInfoUrl = release.ReleaseUrl
    };
}
