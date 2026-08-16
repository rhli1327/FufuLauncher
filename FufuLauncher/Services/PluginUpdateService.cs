/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/
using System.IO.Compression;
using System.Text;
using FufuLauncher.Constants;
using FufuLauncher.Helpers;
using FufuLauncher.Contracts.Services;

namespace FufuLauncher.Services
{
    public interface IPluginUpdateService
    {
        Task ExecuteAutoUpdateAsync(StringBuilder logBuilder);
    }

    public class PluginUpdateService : IPluginUpdateService
    {
        private readonly ILocalSettingsService _localSettingsService;
        public const string AutoUpdatePluginKey = "IsAutoUpdatePluginEnabled";

        public PluginUpdateService(ILocalSettingsService localSettingsService)
        {
            _localSettingsService = localSettingsService;
        }

        public async Task ExecuteAutoUpdateAsync(StringBuilder logBuilder)
        {
            try
            {
                var enabledObj = await _localSettingsService.ReadSettingAsync(AutoUpdatePluginKey);
                if (enabledObj == null || !Convert.ToBoolean(enabledObj)) return;

                logBuilder.AppendLine("[插件更新] 自动更新已启用，开始获取最新普通版插件...");

                string downloadUrl = ApiEndpoints.PluginRawUrl;
                
                string tempPath = Path.Combine(Path.GetTempPath(), "FuFuPlugin_AutoUpdate.zip");
                string extractPath = Path.Combine(Path.GetTempPath(), "FuFuPlugin_AutoUpdate_Extract_" + Guid.NewGuid());
                string pluginsDir = Path.Combine(AppContext.BaseDirectory, "Plugins");
                string targetDir = Path.Combine(pluginsDir, "FuFuPlugin");
                string configPath = Path.Combine(targetDir, "config.ini");
                string backupConfigPath = Path.Combine(Path.GetTempPath(), "config_backup.ini");

                if (File.Exists(configPath))
                {
                    File.Copy(configPath, backupConfigPath, true);
                    logBuilder.AppendLine("[插件更新] 已备份现有预设配置");
                }

                using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
                {
                    var secureUri = DownloadSecurity.RequireHttpsUri(downloadUrl, "插件更新");
                    HttpResponseMessage response = await client.GetAsync(secureUri, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();

                    using (response)
                    using (var stream = await response.Content.ReadAsStreamAsync())
                    using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                    {
                        await stream.CopyToAsync(fileStream);
                    }
                }

                PluginVerifier.VerifyFileHash(tempPath, ApiEndpoints.PluginSha256, "FuFuPlugin bundle");

                if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
                Directory.CreateDirectory(extractPath);
                DownloadSecurity.ExtractZipSafely(tempPath, extractPath);

                var subDirs = Directory.GetDirectories(extractPath);
                string sourceDir = (subDirs.Length == 1 && Directory.GetFiles(extractPath).Length == 0) ? subDirs[0] : extractPath;

                if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
                Directory.CreateDirectory(targetDir);

                foreach (var dirPath in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
                {
                    Directory.CreateDirectory(dirPath.Replace(sourceDir, targetDir));
                }
                foreach (var newPath in Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories))
                {
                    File.Copy(newPath, newPath.Replace(sourceDir, targetDir), true);
                }

                if (File.Exists(backupConfigPath))
                {
                    File.Copy(backupConfigPath, configPath, true);
                    logBuilder.AppendLine("[插件更新] 已将插件默认配置替换为预设配置");
                }

                if (File.Exists(tempPath)) File.Delete(tempPath);
                if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
                if (File.Exists(backupConfigPath)) File.Delete(backupConfigPath);

                logBuilder.AppendLine("[插件更新] 自动更新完成");
            }
            catch (Exception ex)
            {
                logBuilder.AppendLine($"[插件更新] 自动更新失败，将降级使用本地已有插件启动。错误信息: {ex.Message}");
            }
        }
    }
}
