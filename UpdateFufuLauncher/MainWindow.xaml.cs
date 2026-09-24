/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/
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

namespace Updater
{
    public partial class MainWindow : Window
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        private const int DWMWA_MICA_EFFECT = 1029;
        
        private const int DWMSBT_MAINWINDOW = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public int AccentState;
            public int AccentFlags;
            public int GradientColor;
            public int AnimationId;
        }
        
        private const string AppVersion = "1.7.0.0";

        private static readonly HttpClient _httpClient = new(new HttpClientHandler())
        { 
            Timeout = TimeSpan.FromSeconds(15) 
        };
        
        private string _targetExeUrl;
        private string _fileName;
        private string _expectedSha256;
        private List<MirrorInfo> _mirrors = new();
        private DownloadService _downloader;
        private Stopwatch _uiUpdateTimer = new();

        private DispatcherTimer _stuckTimer;
        private long _currentReceivedBytes = 0;
        private long _lastReceivedBytes = 0;
        private int _stuckTicks = 0;
        private bool _isDownloading = false;
        private bool _useThirdPartyCDN = false;
        private bool _isPreviewMode = false;
        private bool _isRollbackMode = false;
        private string _installedVersion = AppVersion;
        private bool _isInstalledPreviewBuild = false;

        private const string TestFileOfficialUrl = "https://raw.githubusercontent.com/moodlehq/moodle-exttests/master/test.html";
        private const string ExpectedTestFileMD5 = "47250a973d1b88d9445f94db4ef2c97a";

        public MainWindow()
        {
            InitializeComponent();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/148.0.0.0 Safari/537.36");

            _stuckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _stuckTimer.Tick += StuckTimer_Tick;
            
            var args = Environment.GetCommandLineArgs();
            foreach (var arg in args)
            {
                if (arg.StartsWith("--use-third-party-cdn=", StringComparison.OrdinalIgnoreCase))
                {
                    var value = arg.Substring("--use-third-party-cdn=".Length);
                    _useThirdPartyCDN = !value.Equals("false", StringComparison.OrdinalIgnoreCase);
                }
                else if (arg.Equals("--preview", StringComparison.OrdinalIgnoreCase))
                {
                    _isPreviewMode = true;
                }
                else if (arg.Equals("--rollback", StringComparison.OrdinalIgnoreCase))
                {
                    _isRollbackMode = true;
                }
                else if (arg.StartsWith("--installed-version=", StringComparison.OrdinalIgnoreCase))
                {
                    _installedVersion = arg.Substring("--installed-version=".Length);
                }
            }

            _isInstalledPreviewBuild = ReleaseUpdateClient.ReadInstalledBuild(AppContext.BaseDirectory)?.Channel == "preview" ||
                _installedVersion.IndexOf("Pre-release", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void StuckTimer_Tick(object sender, EventArgs e)
        {
            if (!_isDownloading || _downloader == null || _downloader.IsCancelled) return;

            long diff = _currentReceivedBytes - _lastReceivedBytes;
            if (diff < 20480) 
            {
                _stuckTicks++;
                if (_stuckTicks >= 10) 
                {
                    _stuckTimer.Stop();
                    var result = MessageBox.Show("下载进度长期未动或网络极慢，建议取消当前下载并更换其他节点尝试\n\n是否立即取消下载？", "下载缓慢", MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (result == MessageBoxResult.Yes)
                    {
                        CancelDownload_Click(null, null);
                    }
                    else
                    {
                        _stuckTicks = 0;
                        _stuckTimer.Start();
                    }
                }
            }
            else
            {
                _stuckTicks = 0;
            }
            _lastReceivedBytes = _currentReceivedBytes;
        }

        private void Window_SourceInitialized(object sender, EventArgs e)
        {
            IntPtr windowHandle = new WindowInteropHelper(this).EnsureHandle();
            
            int useImmersiveDarkMode = 1;
            DwmSetWindowAttribute(windowHandle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useImmersiveDarkMode, Marshal.SizeOf(typeof(int)));

            var osVersion = Environment.OSVersion.Version;
            if (osVersion.Major == 10 && osVersion.Minor == 0 && osVersion.Build >= 22000)
            {
                if (osVersion.Build >= 22621)
                {
                    int backdropType = DWMSBT_MAINWINDOW;
                    DwmSetWindowAttribute(windowHandle, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, Marshal.SizeOf(typeof(int)));
                }
                else
                {
                    int micaValue = 1;
                    DwmSetWindowAttribute(windowHandle, DWMWA_MICA_EFFECT, ref micaValue, Marshal.SizeOf(typeof(int)));
                }
            }
            else
            {
                AccentPolicy accent = new AccentPolicy
                {
                    AccentState = 4, 
                    GradientColor = 0x7F202020 
                };

                int accentSize = Marshal.SizeOf(accent);
                IntPtr accentPtr = Marshal.AllocHGlobal(accentSize);
                try
                {
                    Marshal.StructureToPtr(accent, accentPtr, false);

                    WindowCompositionAttributeData data = new WindowCompositionAttributeData
                    {
                        Attribute = 19, 
                        Data = accentPtr,
                        SizeOfData = accentSize
                    };

                    SetWindowCompositionAttribute(windowHandle, ref data);
                }
                finally
                {
                    Marshal.FreeHGlobal(accentPtr);
                }
            }
        }

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

        private void SelectRelease(PublishedBuild release)
        {
            _targetExeUrl = release.InstallerUrl;
            _fileName = release.InstallerName;
            _expectedSha256 = release.InstallerSha256;
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

            var confirm = MessageBox.Show(
                $"确定要回退到正式版 v{release.VersionLabel}吗？\n\n回退将下载并安装最新正式版，覆盖当前预览版。",
                "回退正式版", MessageBoxButton.YesNo, MessageBoxImage.Question);
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

        private async Task TestMirrorsAsync()
        {
            string[] proxyDomains = {
                "gh.llkk.cc", "github.dpik.top", "gh-proxy.com", "ghfile.geekertao.top",
                "github-proxy.memory-echoes.cn", "cdn.gh-proxy.com", "github.tbedu.top",
                "gh.927223.xyz", "gh.bugdey.us.kg", "git.yylx.win", "cdn.akaere.online",
                "ghproxy.net", "down.mxw.xx.kg", "down.mxw.qzz.io", "github.mxw.qzz.io",
                "gh.dpik.top", "gh.acmsz.top", "gitproxy.127731.xyz", "gh.jjj.gv.uy",
                "github.chenc.dev", "gh.felicity.ac.cn", "gh.inkchills.cn", "gh.jasonzeng.dev",
                "gh.ddlc.top", "gp.zkitefly.eu.org", "gitproxy.mrhjx.cn", "ghfast.top",
                "fastgit.cc", "gh.sixyin.com", "ghp.arslantu.xyz", "github.ednovas.xyz",
                "ghproxy.cxkpro.top", "ghproxy.imciel.com", "gh.idayer.com", "gh-proxy.net",
                "j.1lin.dpdns.org", "github.starrlzy.cn", "ghm.078465.xyz", "ghf.无名氏.top",
                "jiashu.1win.eu.org", "tvv.tw", "j.1win.ggff.net", "gh.catmak.name",
                "gh.b52m.cn", "slink.ltd", "github.tmby.shop", "ghpr.cc", "gh.tryxd.cn",
                "gh.monlor.com", "ghpxy.hwinzniej.top", "git.669966.xyz",
                "github.geekery.cn", "ghproxy.1888866.xyz", "github.xxlab.tech", "free.cn.eu.org",
                "gh.chjina.com", "ghp.keleyaa.com", "proxy.yaoyaoling.net", "ghproxy.monkeyray.net",
                "gh.noki.icu", "g.blfrp.cn"
            };

            int completed = 0;
            var tasks = proxyDomains.Select(async domain =>
            {
                try
                {
                    string testProxyUrl = $"https://{domain}/{TestFileOfficialUrl}";
                    string exeProxyUrl = $"https://{domain}/{_targetExeUrl}";
                    
                    var result = await VerifyAndMeasureNodeAsync(domain, testProxyUrl, exeProxyUrl);
                    
                    int current = Interlocked.Increment(ref completed);
                    Dispatcher.InvokeAsync(() =>
                    {
                        if (TestStatusText != null)
                        {
                            TestStatusText.Text = $"正在测速中...\n{current} / {proxyDomains.Length}";
                        }
                    });
                    
                    return result;
                }
                catch
                {
                    Interlocked.Increment(ref completed);
                    return new MirrorInfo { Domain = domain, IsSuccess = false, ResponseTimeMs = long.MaxValue };
                }
            }).ToList();

            var results = await Task.WhenAll(tasks);
            _mirrors = results.Where(r => r.IsSuccess).OrderBy(r => r.ResponseTimeMs).ToList();
        }

        private async Task<MirrorInfo> VerifyAndMeasureNodeAsync(string domain, string testUrl, string exeUrl)
        {
            var info = new MirrorInfo { Domain = domain, FullUrl = exeUrl, IsSuccess = false, ResponseTimeMs = long.MaxValue };
            var stopwatch = new Stopwatch();
            try
            {
                stopwatch.Start();
                using var response = await _httpClient.GetAsync(testUrl, HttpCompletionOption.ResponseContentRead);
                response.EnsureSuccessStatusCode();
                
                byte[] fileData = await response.Content.ReadAsByteArrayAsync();
                stopwatch.Stop();

                string hash = CalculateMD5(fileData);
                if (hash.Equals(ExpectedTestFileMD5, StringComparison.OrdinalIgnoreCase))
                {
                    info.IsSuccess = true;
                    info.ResponseTimeMs = stopwatch.ElapsedMilliseconds;
                    double speedKbps = (fileData.Length / 1024.0) / (info.ResponseTimeMs / 1000.0);
                    info.StatusDesc = $"{info.ResponseTimeMs} ms | {(speedKbps > 500 ? "极佳" : "正常")}";
                }
            }
            catch
            {
                stopwatch.Stop();
                info.IsSuccess = false;
                info.ResponseTimeMs = long.MaxValue;
            }
            return info;
        }

        private string CalculateMD5(byte[] data)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] hashBytes = md5.ComputeHash(data);
                StringBuilder sb = new();
                for (int i = 0; i < hashBytes.Length; i++)
                {
                    sb.Append(hashBytes[i].ToString("x2"));
                }
                return sb.ToString();
            }
        }
        
        private bool VerifyFileSha256(string filePath)
        {
            if (string.IsNullOrEmpty(_expectedSha256))
            {
                return true;
            }

            try
            {
                using var stream = File.OpenRead(filePath);
                byte[] hashBytes = SHA256.HashData(stream);
                StringBuilder sb = new();
                foreach (var b in hashBytes)
                {
                    sb.Append(b.ToString("x2"));
                }
                string actualHash = sb.ToString();

                return string.Equals(actualHash, _expectedSha256, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private void CloseWindow_Click(object sender, RoutedEventArgs e)
        {
            Environment.Exit(0);
        }

        private void DirectDownload_Click(object sender, RoutedEventArgs e)
        {
            StartMultiThreadDownload(_targetExeUrl);
        }

        private void MirrorDownload_Click(object sender, RoutedEventArgs e)
        {
            if (MirrorListView.SelectedItem is MirrorInfo selectedMirror)
            {
                StartMultiThreadDownload(selectedMirror.FullUrl);
            }
            else
            {
                MessageBox.Show("请先在列表中选择一个可用节点", "提示");
            }
        }

        private void StartMultiThreadDownload(string url)
        {
            SelectionPanel.Visibility = Visibility.Collapsed;
            ActionPanel.Visibility = Visibility.Collapsed;
            DownloadProgressPanel.Visibility = Visibility.Visible;
            SubtitleText.Text = "正在获取更新...";
            DownloadMainText.Text = "正在下载更新文件...";
            
            if (CancelBtn != null) CancelBtn.IsEnabled = true;

            string savePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _fileName);

            var downloadOpt = new DownloadConfiguration()
            {
                ChunkCount = 4, 
                ParallelDownload = true,
                MaxTryAgainOnFailure = 9999,
                ClearPackageOnCompletionWithFailure = false,
                CustomHttpClientFactory = () => new HttpClient(new HttpClientHandler())
                { 
                    Timeout = TimeSpan.FromSeconds(30) 
                }
            };

            _downloader = new DownloadService(downloadOpt);
            
            _currentReceivedBytes = 0;
            _lastReceivedBytes = 0;
            _stuckTicks = 0;
            _isDownloading = true;

            _uiUpdateTimer.Restart();
            _stuckTimer.Start();

            _downloader.DownloadProgressChanged += (s, e) =>
            {
                _currentReceivedBytes = e.ReceivedBytesSize;

                if (_uiUpdateTimer.ElapsedMilliseconds >= 100 || e.ProgressPercentage >= 100)
                {
                    _uiUpdateTimer.Restart();

                    double downloadedMB = e.ReceivedBytesSize / 1024.0 / 1024.0;
                    double totalMB = e.TotalBytesToReceive / 1024.0 / 1024.0;
                    double speedMB = e.BytesPerSecondSpeed / 1024.0 / 1024.0;
                    double progress = e.ProgressPercentage;

                    Dispatcher.InvokeAsync(() =>
                    {
                        DownloadProgressBar.Value = progress;
                        DownloadDetailText.Text = $"{downloadedMB:F2} MB / {totalMB:F2} MB ({progress:F1}%) • {speedMB:F2} MB/s";
                    }, System.Windows.Threading.DispatcherPriority.Background);
                }
            };

            _downloader.DownloadFileCompleted += (s, e) =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    _isDownloading = false;
                    _stuckTimer.Stop();
                    _uiUpdateTimer.Stop();

                    if (e.Cancelled)
                    {
                        ResetToSelectionUI();
                    }
                    else if (e.Error != null)
                    {
                        MessageBox.Show($"错误: {e.Error.Message}\n\n请选择其他节点继续下载\n或者去这里下载：https://wwaoi.lanzn.com/b00wnb99ef\n密码:6hnh", "失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                        ResetToSelectionUI();
                    }
                    else
                    {
                        DownloadMainText.Text = "正在校验文件完整性";

                        if (!VerifyFileSha256(savePath))
                        {
                            try { File.Delete(savePath); } catch { }

                            MessageBox.Show(
                                "下载的文件SHA-256校验失败，文件可能在传输过程中被损坏或篡改\n\n已自动删除该文件，请更换其他节点重新下载",
                                "文件完整性校验失败",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);

                            ResetToSelectionUI();
                            return;
                        }

                        DownloadMainText.Text = "正在启动安装...";
                        
                        ProcessStartInfo psi = new()
                        {
                            FileName = savePath,
                            UseShellExecute = true,
                            Verb = "runas"
                        };
                        try
                        {
                            Process.Start(psi);
                        }
                        catch { }
                        
                        Environment.Exit(0);
                    }
                });
            };

            _downloader.DownloadFileTaskAsync(url, savePath);
        }

        private void CancelDownload_Click(object sender, RoutedEventArgs e)
        {
            if (CancelBtn != null) CancelBtn.IsEnabled = false;

            if (_downloader != null && !_downloader.IsCancelled)
            {
                DownloadMainText.Text = "正在取消下载...";
                _downloader.CancelAsync();
            }
        }

        private void ResetToSelectionUI()
        {
            DownloadProgressPanel.Visibility = Visibility.Collapsed;
            SelectionPanel.Visibility = Visibility.Visible;
            ActionPanel.Visibility = Visibility.Visible;
            SubtitleText.Text = "下载已暂停";

            if (CancelBtn != null) CancelBtn.IsEnabled = true;
        }
    }

    public class MirrorInfo
    {
        public string Domain { get; set; }
        public string FullUrl { get; set; }
        public bool IsSuccess { get; set; }
        public long ResponseTimeMs { get; set; }
        public string StatusDesc { get; set; }
    }
}
