/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/
using System.IO.Compression;
using FufuLauncher.Constants;
using FufuLauncher.Helpers;
using Windows.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

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
        string urlLatest = ApiEndpoints.PluginRawUrl;
        
        var stackPanel = new StackPanel { Spacing = 10 };
        
        var rbLatest = new RadioButton { Content = "下载/更新插件(国际服通用)", IsChecked = true, GroupName = "PluginSelect", Tag = urlLatest };
        
        var warningText = new TextBlock 
        { 
            Text = "注意：最新体验版插件已内置手柄热切换和已适配国际服，且功能全面和性能可观", 
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Margin = new Thickness(0, 5, 0, 5)
        };
        
        stackPanel.Children.Add(new TextBlock { Text = "请选择要下载并安装的插件包：", Margin = new Thickness(0, 0, 0, 5) });
        stackPanel.Children.Add(rbLatest);
        stackPanel.Children.Add(warningText);
        stackPanel.Children.Add(new TextBlock 
        { 
            Text = "使用已固定提交和 SHA-256 的 GitHub HTTPS 插件包",
            FontSize = 12, 
            Opacity = 0.7,
            Margin = new Thickness(0, 10, 0, 0)
        });

        var dialog = new ContentDialog
        {
            Title = "获取插件",
            Content = stackPanel,
            PrimaryButtonText = "下载并安装",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await DownloadAndInstallPluginAsync(urlLatest);
        }
    }
    
    private async Task DownloadAndInstallPluginAsync(string downloadUrl)
    {
        var secureUri = DownloadSecurity.RequireHttpsUri(downloadUrl, "插件下载");
        var fileName = secureUri.Segments.Last();
        if (fileName.Contains("?")) fileName = fileName.Split('?')[0];
        if (string.IsNullOrEmpty(fileName) || !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) 
            fileName = "CustomPlugin.zip";
        
        var rawGithubUrl = secureUri.ToString();
        
        var tempPath = Path.Combine(Path.GetTempPath(), fileName);
        var extractPath = Path.Combine(Path.GetTempPath(), Path.GetFileNameWithoutExtension(fileName) + "_Extract_" + Guid.NewGuid());
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
            Text = "正在连接...", HorizontalAlignment = HorizontalAlignment.Center 
        };
        var stackPanel = new StackPanel();
        stackPanel.Children.Add(statusText);
        stackPanel.Children.Add(progressBar);

        var progressDialog = new ContentDialog
        {
            Title = $"正在获取 {fileName}",
            Content = stackPanel,
            CloseButtonText = null,
            XamlRoot = XamlRoot
        };

        progressDialog.ShowAsync();

        try
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);

            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                HttpResponseMessage response = await client.GetAsync(secureUri, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode)
                    throw new Exception($"下载失败 (HTTP {response.StatusCode})");
                
                using (response)
                {
                    var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                    var totalRead = 0L;
                    var buffer = new byte[8192];
                    var isMoreToRead = true;
                    
                    using (var stream = await response.Content.ReadAsStreamAsync())
                    using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
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
                                    statusText.Text = $"GitHub HTTPS 下载中... {percent}%";
                                }
                            }
                        }
                    }
                }
            }

            if (string.Equals(secureUri.ToString(), ApiEndpoints.PluginRawUrl, StringComparison.OrdinalIgnoreCase))
                Services.PluginVerifier.VerifyFileHash(tempPath, ApiEndpoints.PluginSha256, "FuFuPlugin bundle");
            
            statusText.Text = "正在解压...";
            progressBar.IsIndeterminate = true;
            await Task.Delay(500); 
            
            if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
            Directory.CreateDirectory(extractPath);

            await Task.Run(() => DownloadSecurity.ExtractZipSafely(tempPath, extractPath));
            
            try { File.Delete(tempPath); }
            catch
            {
                // ignored
            }

            statusText.Text = "正在安装...";
            
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
            var failDialog = new ContentDialog
            {
                Title = "下载/安装错误",
                Content = $"自动下载失败：{ex.Message}\n\n建议点击下方按钮打开浏览器手动下载。",
                PrimaryButtonText = "手动下载",
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };
            if (await failDialog.ShowAsync() == ContentDialogResult.Primary)
            {
                try { await Launcher.LaunchUriAsync(new Uri(rawGithubUrl)); }
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
