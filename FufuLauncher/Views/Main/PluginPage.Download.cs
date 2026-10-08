/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/

using System.IO.Compression;
using Windows.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using FufuLauncher.Helpers;
using FufuLauncher.Services;

namespace FufuLauncher.Views;

public sealed partial class PluginPage
{
    #region 插件下载与安装

    private void MoveDirectorySafe(string sourceDir, string destDir)
    {
        var parentDir = Path.GetDirectoryName(destDir);
        if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }

        if (Path.GetPathRoot(sourceDir)!.Equals(Path.GetPathRoot(destDir), StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(sourceDir, destDir);
            return;
        }

        if (!Directory.Exists(destDir))
        {
            Directory.CreateDirectory(destDir);
        }

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, true);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var destSubDir = Path.Combine(destDir, Path.GetFileName(dir));
            MoveDirectorySafe(dir, destSubDir);
        }

        Directory.Delete(sourceDir, true);
    }

    private async void OnGetPluginsClick(object sender, RoutedEventArgs e)
    {
        if (App.GetService<LightweightPluginService>().IsLightweightMode)
        {
            await ShowInstallBlockedDialogAsync("LightweightMode_BlockMainInstall".GetLocalized());
            return;
        }
        var dialog = new ContentDialog
        {
            Title = "PluginPage_GetPluginsTitle".GetLocalized(),
            Content = "PluginPage_DownloadNotice".GetLocalized(),
            PrimaryButtonText = "PluginPage_DownloadAndInstallBtn".GetLocalized(),
            CloseButtonText = "CancelBtn".GetLocalized(),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await DownloadAndInstallPluginAsync(Constants.ApiEndpoints.PluginRawUrl);
    }

    private async Task ShowInstallBlockedDialogAsync(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "AdminWarningTitle".GetLocalized(),
            Content = message,
            CloseButtonText = "CloseBtn".GetLocalized(),
            XamlRoot = XamlRoot
        };

        await dialog.ShowAsync();
    }

    private async Task ShowLockedFileDialogAsync(string lockedFilePath)
    {
        var dialog = new ContentDialog
        {
            Title = FileLockHelper.GetLockedFileTitle(),
            Content = FileLockHelper.GetLockedFileMessage(lockedFilePath),
            CloseButtonText = "CloseBtn".GetLocalized(),
            XamlRoot = XamlRoot
        };

        await dialog.ShowAsync();
    }

    private async Task DownloadAndInstallPluginAsync(string downloadUrl)
    {
        var lightweightPlugin = App.GetService<LightweightPluginService>();

        var secureUri = Helpers.DownloadSecurity.RequireHttpsUri(downloadUrl, "插件下载");
        if (!string.Equals(secureUri.AbsoluteUri, Constants.ApiEndpoints.PluginRawUrl, StringComparison.Ordinal))
            throw new InvalidOperationException("仅允许下载已固定校验值的主插件包。");
        var fileName = secureUri.Segments.Last();
        if (fileName.Contains("?")) fileName = fileName.Split('?')[0];
        if (string.IsNullOrEmpty(fileName) || !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            fileName = "CustomPlugin.zip";

        var preBlockReason = lightweightPlugin.GetInstallBlockReason(Path.GetFileNameWithoutExtension(fileName), null);
        if (preBlockReason != null)
        {
            await ShowInstallBlockedDialogAsync(preBlockReason);
            return;
        }

        var tempPath = Path.Combine(Path.GetTempPath(), fileName);
        var extractPath = Path.Combine(Path.GetTempPath(),
            Path.GetFileNameWithoutExtension(fileName) + "_Extract_" + Guid.NewGuid());
        var pluginsDir = Path.Combine(AppContext.BaseDirectory, "Plugins");
        if (!Directory.Exists(pluginsDir))
        {
            Directory.CreateDirectory(pluginsDir);
        }

        var progressBar = new ProgressBar
        {
            Minimum = 0, Maximum = 100, Value = 0, Height = 20, Margin = new Thickness(0, 10, 0, 0)
        };
        var statusText = new TextBlock
        {
            Text = "PluginPage_StatusConnecting".GetLocalized(), HorizontalAlignment = HorizontalAlignment.Center
        };
        var stackPanel = new StackPanel();
        stackPanel.Children.Add(statusText);
        stackPanel.Children.Add(progressBar);

        var progressDialog = new ContentDialog
        {
            Title = string.Format("PluginPage_FetchingFormat".GetLocalized(), fileName),
            Content = stackPanel,
            CloseButtonText = null,
            XamlRoot = XamlRoot
        };

        progressDialog.ShowAsync();

        string? installTargetDir = null;

        try
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);

            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                var response = await client.GetAsync(secureUri, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                bool usedFallback = false;

                using (response)
                {
                    var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                    var totalRead = 0L;
                    var buffer = new byte[8192];
                    var isMoreToRead = true;

                    using (var stream = await response.Content.ReadAsStreamAsync())
                    using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                               8192, true))
                    {
                        while (isMoreToRead)
                        {
                            var read = await stream.ReadAsync(buffer, 0, buffer.Length);
                            if (read == 0) isMoreToRead = false;
                            else
                            {
                                await fileStream.WriteAsync(buffer, 0, read);
                                totalRead += read;
                                if (totalBytes != -1)
                                {
                                    var percent = Math.Round((double)totalRead / totalBytes * 100, 0);

                                    progressBar.Value = percent;
                                    var source = usedFallback
                                        ? "PluginPage_LineBackup".GetLocalized()
                                        : "PluginPage_LineMain".GetLocalized();
                                    statusText.Text = string.Format("PluginPage_StatusDownloadingFormat".GetLocalized(),
                                        source, percent);
                                }
                            }
                        }
                    }
                }
            }

            statusText.Text = "PluginPage_StatusExtracting".GetLocalized();
            progressBar.IsIndeterminate = true;
            await Task.Delay(500);

            if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
            Directory.CreateDirectory(extractPath);

            Services.PluginVerifier.VerifyFileHash(tempPath, Constants.ApiEndpoints.PluginSha256, "FuFuPlugin bundle");
            Helpers.DownloadSecurity.ExtractZipSafely(tempPath, extractPath);

            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // ignored
            }

            statusText.Text = "PluginPage_StatusInstalling".GetLocalized();

            var targetFolderName = Path.GetFileNameWithoutExtension(tempPath);
            var finalDestDir = Path.Combine(pluginsDir, targetFolderName);

            var subDirs = Directory.GetDirectories(extractPath);
            var files = Directory.GetFiles(extractPath);

            string sourceDirToMove;

            if (subDirs.Length == 1 && files.Length == 0)
            {
                sourceDirToMove = subDirs[0];
                targetFolderName = new DirectoryInfo(sourceDirToMove).Name;
                finalDestDir = Path.Combine(pluginsDir, targetFolderName);
            }
            else
            {
                sourceDirToMove = extractPath;
            }

            var packageDllName = Directory.GetFiles(sourceDirToMove, "*.dll", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .FirstOrDefault();

            var blockReason = lightweightPlugin.GetInstallBlockReason(targetFolderName, packageDllName);
            if (blockReason != null)
            {
                progressDialog.Hide();
                await ShowInstallBlockedDialogAsync(blockReason);
                return;
            }

            installTargetDir = finalDestDir;

            var lockedFile = FileLockHelper.FindLockedFileInDirectory(finalDestDir);
            if (lockedFile != null)
            {
                progressDialog.Hide();
                await ShowLockedFileDialogAsync(lockedFile);
                return;
            }

            if (Directory.Exists(finalDestDir))
            {
                Directory.Delete(finalDestDir, true);
            }

            await Task.Run(() => MoveDirectorySafe(sourceDirToMove, finalDestDir));

            try
            {
                if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
            }
            catch
            {
                // ignored
            }

            ViewModel.StatusMessage = $"{targetFolderName} 安装成功！";
            ViewModel.LoadPlugins();

            progressDialog.Hide();
        }
        catch (Exception ex)
        {
            progressDialog.Hide();

            var lockedFile = FileLockHelper.FindLockedFileInDirectory(installTargetDir);
            if (lockedFile != null)
            {
                await ShowLockedFileDialogAsync(lockedFile);
                return;
            }

            var failDialog = new ContentDialog
            {
                Title = "PluginPage_DownloadErrorTitle".GetLocalized(),
                Content = string.Format("PluginPage_DownloadErrorContentFormat".GetLocalized(), ex.Message),
                PrimaryButtonText = "PluginPage_ManualDownloadBtn".GetLocalized(),
                CloseButtonText = "CloseBtn".GetLocalized(),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };
            if (await failDialog.ShowAsync() == ContentDialogResult.Primary)
            {
                try
                {
                    await Launcher.LaunchUriAsync(new Uri(downloadUrl));
                }
                catch
                {
                    // ignored
                }
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
                if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
            }
            catch
            {
                // ignored
            }
        }
    }

    #endregion
}