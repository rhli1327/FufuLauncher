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

    public async Task<UpdateProbeResult> ProbeUpdateAsync(CancellationToken cancellationToken = default)
    {
        var result = new UpdateProbeResult
        {
            CurrentVersion = AppVersionHelper.NumericVersion,
            IsPreviewBuild = AppVersionHelper.IsPreviewBuild
        };
        try
        {
            var installed = ReleaseUpdateClient.ReadInstalledBuild(AppContext.BaseDirectory);
            var stable = await _releaseClient.GetLatestAsync().WaitAsync(cancellationToken);
            var preview = await _releaseClient.GetLatestAsync(preview: true).WaitAsync(cancellationToken);
            result.Succeeded = true;
            result.IsDevBuild = await _devBuildDetectionService.DetectAsync(result.CurrentVersion);
            result.ServerVersion = stable?.VersionLabel ?? string.Empty;
            result.PreviewVersion = preview?.VersionLabel ?? string.Empty;
            result.UpdateInfoUrl = stable?.ReleaseUrl ?? string.Empty;
            result.PreviewUpdateInfoUrl = preview?.ReleaseUrl ?? string.Empty;
            result.HasUpdate = stable != null && ReleaseUpdateClient.HasUpdate(installed, stable.Build);
            result.HasPreviewUpdate = preview != null &&
                (stable == null || preview.Build.BuiltAtUtc > stable.Build.BuiltAtUtc) &&
                ReleaseUpdateClient.HasUpdate(installed, preview.Build);
            result.Message = result.HasUpdate || result.HasPreviewUpdate
                ? "发现新的加固版构建。" : "当前已是最新加固版构建。";
        }
        catch (OperationCanceledException)
        {
            result.Message = "检查更新已取消或超时。";
        }
        catch (Exception ex)
        {
            result.Message = $"检查更新失败：{ex.Message}";
        }
        return result;
    }

    public async Task<string> GetUpdateAnnouncementUrlAsync()
    {
        var release = await _releaseClient.GetLatestAsync(preview: AppVersionHelper.IsPreviewBuild);
        return release?.ReleaseUrl ?? $"https://github.com/{ReleaseUpdateClient.Repository}/releases";
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
