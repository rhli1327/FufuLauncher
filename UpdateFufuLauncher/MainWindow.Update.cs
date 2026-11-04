/* Copyright (c) FufuLauncher Dev Team. Licensed under the MIT License. */
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Downloader;
using FufuLauncher.Updates;

namespace Updater;
public partial class MainWindow
{
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_isRollbackMode)
            {
                await CheckRollbackAsync();
                return;
            }

            SubtitleText.Text = "正在检查更新...";
            var release = await new ReleaseUpdateClient(_httpClient).GetLatestAsync(_isPreviewMode);
            if (release == null)
            {
                ShowNoUpdate(_isPreviewMode ? "暂未发布预览版更新" : "暂未发布正式版更新", _isInstalledPreviewBuild);
                return;
            }

            var installed = ReleaseUpdateClient.ReadInstalledBuild(AppContext.BaseDirectory);
            if (!ReleaseUpdateClient.HasUpdate(installed, release.Build))
            {
                ShowNoUpdate("当前已是最新，无需更新", _isInstalledPreviewBuild);
                return;
            }

            SelectRelease(release);
            await PrepareDownloadAsync("请选择下载线路", "直连GitHub下载...");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"更新初始化失败，请检查网络，或前往加固版发布页下载\nhttps://github.com/{ReleaseUpdateClient.Repository}/releases\n错误详情: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            Environment.Exit(1);
        }
    }

    private void ShowNoUpdate(string message, bool showRollback = false)
    {
        LoadingPanel.Visibility = Visibility.Collapsed;
        NoUpdatePanel.Visibility = Visibility.Visible;
        NoUpdateText.Text = message;
        RollbackBtn.Visibility = showRollback ? Visibility.Visible : Visibility.Collapsed;
        SubtitleText.Text = "检查完毕";
    }

    private async Task PrepareDownloadAsync(string selectionSubtitle, string directSubtitle)
    {
        if (_useThirdPartyCDN)
        {
            SubtitleText.Text = "节点联通性校验...";
            await TestMirrorsAsync();
            LoadingPanel.Visibility = Visibility.Collapsed;
            SelectionPanel.Visibility = Visibility.Visible;
            ActionPanel.Visibility = Visibility.Visible;
            MirrorListView.ItemsSource = _mirrors;
            if (_mirrors.Count > 0)
            {
                MirrorListView.SelectedIndex = 0;
                SubtitleText.Text = selectionSubtitle;
            }
            else
            {
                SubtitleText.Text = "所有节点均未通过校验";
                MessageBox.Show("所有镜像节点均未通过文件完整性校验或网络超时\n\n请更换网络环境，或尝试使用直连官方源下载", "网络不佳", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        else
        {
            // Skip mirror testing, directly download from GitHub
            SubtitleText.Text = directSubtitle;
            StartMultiThreadDownload(_targetExeUrl);
        }
    }

    private async Task CheckRollbackAsync()
    {
        SubtitleText.Text = "准备回退正式版...";
        var release = await new ReleaseUpdateClient(_httpClient).GetLatestAsync();
        if (release == null)
        {
            ShowNoUpdate("暂未发布正式版");
            return;
        }

        var confirm = MessageBox.Show($"确定要回退到正式版 v{release.VersionLabel}吗？\n\n回退将下载并安装最新正式版，覆盖当前预览版。", "回退正式版", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            Environment.Exit(0);
            return;
        }

        SelectRelease(release);
        await PrepareDownloadAsync("请选择下载线路（回退正式版）", "直连GitHub下载正式版...");
    }

    private async void RollbackToStable_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await CheckRollbackAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"回退正式版失败，请检查网络\n错误详情: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            Environment.Exit(1);
        }
    }
}
