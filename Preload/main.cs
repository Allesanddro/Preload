using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Preload
{
    public partial class main : Form
    {
        private const string _githubRepo = "Allesanddro/Preload";
        private const string _currentVersionString = "1.0.7";

        private ProgressBar downloadProgressBar;

        private CancellationTokenSource _preloadCts;
        private CancellationTokenSource _calculationCts;
        private string[] _fileList;
        private long _totalFolderSize;
        private bool _isCalculating;
        private bool _isPreloading;

        public main()
        {
            InitializeComponent();

            this.Text = $"PrimoCache Preloader v{_currentVersionString}";

            this.Load += main_Load;
            this.Resize += main_Resize;
            this.FormClosing += main_FormClosing;
            this.AllowDrop = true;
            this.DragEnter += main_DragEnter;
            this.DragDrop += main_DragDrop;

            this.chkAutoUpdate.CheckedChanged += chkAutoUpdate_CheckedChanged;
        }


        private void main_Resize(object sender, EventArgs e)
        {
            if (this.WindowState == FormWindowState.Minimized)
            {
                this.Hide();
                this.ShowInTaskbar = false;
                trayIcon.Visible = true;
                trayIcon.ShowBalloonTip(1000, "Preloader Minimized", "The application is still running in the system tray.", ToolTipIcon.Info);
            }
        }

        private void ShowWindow()
        {
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.ShowInTaskbar = true;
            trayIcon.Visible = false;
        }

        private void trayIcon_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            ShowWindow();
        }

        private void showToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ShowWindow();
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Application.Exit();
        }

        private void main_FormClosing(object sender, FormClosingEventArgs e)
        {
            trayIcon.Visible = false;
            trayIcon.Dispose();
        }

        private void main_Load(object sender, EventArgs e)
        {
            Log("Application started.");
            UpdateStatusLabels(true);

            chkAutoUpdate.Checked = Properties.Settings.Default.AutoUpdateCheck;
            if (chkAutoUpdate.Checked)
            {
                Log("Auto-update enabled. Checking for new version on startup...");
                btnUpdateCheck_Click(this, EventArgs.Empty);
            }
        }

        private void chkAutoUpdate_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.AutoUpdateCheck = chkAutoUpdate.Checked;
            Properties.Settings.Default.Save();
            Log($"Auto-update check set to: {chkAutoUpdate.Checked}");
        }

        private async void btnUpdateCheck_Click(object sender, EventArgs e)
        {
            bool isAutoCheck = (sender == this);

            if (!isAutoCheck)
            {
                Log("Checking for updates...");
                btnUpdateCheck.Enabled = false;
                btnUpdateCheck.Text = "Checking...";
            }

            string url = $"https://api.github.com/repos/{_githubRepo}/releases/latest";

            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "Preloader-Update-Check");
                    string json = await client.GetStringAsync(url);

                    string latestTag = GetJsonValue(json, "tag_name");
                    Version latestVersion = new Version(latestTag.TrimStart('v'));
                    Version currentVersion = new Version(_currentVersionString);

                    if (!isAutoCheck)
                    {
                        Log($"Current version: {currentVersion}. Latest version on GitHub: {latestVersion}");
                    }

                    if (latestVersion > currentVersion)
                    {
                        var result = MessageBox.Show(
                            $"A new version ({latestTag}) is available!\n\nYou are currently on v{currentVersion}.\n\nWould you like to download and install the update now?",
                            "Update Available",
                            MessageBoxButtons.YesNoCancel,
                            MessageBoxIcon.Information);

                        if (result == DialogResult.Yes)
                        {
                            string assetUrl = GetDownloadUrl(json);
                            if (string.IsNullOrEmpty(assetUrl))
                            {
                                throw new Exception("Could not find a .zip asset in the latest release.");
                            }

                            await DownloadAndApplyUpdate(assetUrl);
                        }
                    }
                    else
                    {
                        if (!isAutoCheck)
                        {
                            MessageBox.Show("You are running the latest version.", "Up to Date", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        else
                        {
                            Log("Application is up to date.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"ERROR: Could not check for updates. {ex.Message}");
                if (!isAutoCheck)
                {
                    MessageBox.Show($"Could not check for updates.\nPlease check your internet connection or visit the GitHub page manually.\n\nError: {ex.Message}", "Update Check Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            finally
            {
                if (!isAutoCheck)
                {
                    btnUpdateCheck.Enabled = true;
                    btnUpdateCheck.Text = "Check for Updates...";
                }
            }
        }

        private async Task DownloadAndApplyUpdate(string downloadUrl)
        {
            Log("Starting update download...");
            SetDownloadingState(true);

            string downloadPath = Path.Combine(Path.GetTempPath(), "Preloader-Update.zip");
            string extractPath = Path.Combine(Path.GetTempPath(), "Preloader-Update");

            try
            {
                using (var client = new HttpClient())
                {
                    var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();

                    var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                    var totalBytesRead = 0L;

                    using (var contentStream = await response.Content.ReadAsStreamAsync())
                    using (var fileStream = new FileStream(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                    {
                        var buffer = new byte[8192];
                        int bytesRead;
                        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            await fileStream.WriteAsync(buffer, 0, bytesRead);
                            totalBytesRead += bytesRead;
                            if (totalBytes != -1)
                            {
                                downloadProgressBar.Value = (int)(100 * totalBytesRead / totalBytes);
                            }
                        }
                    }
                }

                Log("Download complete. Extracting update...");

                if (Directory.Exists(extractPath))
                {
                    Directory.Delete(extractPath, true);
                }
                ZipFile.ExtractToDirectory(downloadPath, extractPath);

                Log("Extraction complete. Preparing to apply update...");

                string currentExeName = Path.GetFileName(Process.GetCurrentProcess().MainModule.FileName);

                string newExePath = Directory.GetFiles(extractPath, currentExeName, SearchOption.AllDirectories).FirstOrDefault();

                if (string.IsNullOrEmpty(newExePath))
                {
                    throw new FileNotFoundException("New executable not found in the downloaded zip file.", currentExeName);
                }

                string currentExePath = Process.GetCurrentProcess().MainModule.FileName;
                string batchScriptPath = Path.Combine(Path.GetTempPath(), "updater.bat");
                string scriptContent = $@"
@echo off
echo Waiting for Preloader to close...
timeout /t 2 /nobreak > nul
echo Replacing files...
move /Y ""{newExePath}"" ""{currentExePath}""
echo Cleaning up...
rmdir /s /q ""{extractPath}""
del ""{downloadPath}""
echo Update complete. Relaunching...
start """" ""{currentExePath}""
del ""{batchScriptPath}""
";
                File.WriteAllText(batchScriptPath, scriptContent);

                Process.Start(new ProcessStartInfo(batchScriptPath) { CreateNoWindow = true });
                Application.Exit();
            }
            catch (Exception ex)
            {
                Log($"FATAL: Update failed. {ex.Message}");
                MessageBox.Show($"Update failed: {ex.Message}\n\nPlease try updating manually from GitHub.", "Update Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetDownloadingState(false);
            }
        }

        private void SetDownloadingState(bool isDownloading)
        {
            this.Controls.Cast<Control>().Except(new Control[] { txtLog }).ToList().ForEach(c => c.Enabled = !isDownloading);

            if (isDownloading)
            {
                if (downloadProgressBar == null)
                {
                    downloadProgressBar = new ProgressBar
                    {
                        Name = "downloadProgressBar",
                        Dock = DockStyle.Bottom,
                        Height = 20
                    };
                    this.Controls.Add(downloadProgressBar);
                }
                downloadProgressBar.Visible = true;
                Log("Downloading update... UI will be disabled.");
            }
            else
            {
                if (downloadProgressBar != null)
                {
                    downloadProgressBar.Visible = false;
                }
            }
        }

        private void Log(string message)
        {
            if (txtLog.InvokeRequired)
            {
                txtLog.Invoke((MethodInvoker)delegate { Log(message); });
                return;
            }
            string formattedMessage = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
            txtLog.AppendText(formattedMessage);
            txtLog.ScrollToCaret();
        }

        #region Existing Code
        private async void CalculateFolderSizeAsync(string folderPath)
        {
            _calculationCts?.Cancel();
            _calculationCts = new CancellationTokenSource();
            var token = _calculationCts.Token;

            _fileList = new string[0];
            _totalFolderSize = 0;

            try
            {
                _isCalculating = true;
                SetUICalculatingState(true);
                Log($"Calculating size for folder: {folderPath}");

                _fileList = await Task.Run(() => Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories), token);
                _totalFolderSize = await Task.Run(() => _fileList.Sum(file => {
                    try { return new FileInfo(file).Length; } catch { return 0; }
                }), token);

                token.ThrowIfCancellationRequested();

                string logMessage = $"Calculation complete. Found {_fileList.Length} files. Total size: {FormatBytes(_totalFolderSize)}.";
                Log(logMessage);
                lblSize.Text = $"Total Size: {FormatBytes(_totalFolderSize)}";
                lblProgress.Text = "Ready to Preload";
                lblStatus.Text = "Status: Ready.";
            }
            catch (OperationCanceledException)
            {
                Log("Calculation was canceled.");
                UpdateStatusLabels(true);
            }
            catch (Exception ex)
            {
                Log($"ERROR: Could not calculate folder size. {ex.Message}");
                MessageBox.Show($"Could not calculate folder size: {ex.Message}", "Calculation Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatusLabels(true);
            }
            finally
            {
                _isCalculating = false;
                SetUICalculatingState(false);
                _calculationCts.Dispose();
                _calculationCts = null;
            }
        }

        private void btnBrowse_Click(object sender, EventArgs e)
        {
            using (var folderBrowserDialog = new FolderBrowserDialog())
            {
                folderBrowserDialog.Description = "Select the folder to preload";
                folderBrowserDialog.SelectedPath = txtFolderPath.Text;
                if (folderBrowserDialog.ShowDialog() == DialogResult.OK)
                {
                    string selectedPath = folderBrowserDialog.SelectedPath;
                    txtFolderPath.Text = selectedPath;
                    Log($"Folder selected via browse: {selectedPath}");
                    CalculateFolderSizeAsync(selectedPath);
                }
            }
        }

        private void main_DragDrop(object sender, DragEventArgs e)
        {
            string[] paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (paths != null && paths.Length > 0)
            {
                if (Directory.Exists(paths[0]))
                {
                    string droppedPath = paths[0];
                    txtFolderPath.Text = droppedPath;
                    Log($"Folder selected via drag-drop: {droppedPath}");
                    CalculateFolderSizeAsync(droppedPath);
                }
            }
        }

        private async void btnPreload_Click(object sender, EventArgs e)
        {
            if (_isCalculating)
            {
                MessageBox.Show("Please wait, folder size is still being calculated.", "Busy", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (_fileList == null || _fileList.Length == 0)
            {
                MessageBox.Show("Please select a valid folder with files first.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Log($"Preload started for {_fileList.Length} files.");

            _preloadCts = new CancellationTokenSource();
            var token = _preloadCts.Token;

            _isPreloading = true;
            SetUIPreloadingState(true);

            try
            {
                var stopwatch = new Stopwatch();
                long totalBytesRead = 0;
                stopwatch.Start();

                await Task.Run(() =>
                {
                    byte[] buffer = new byte[81920];
                    int bytesReadFromStream;

                    foreach (var file in _fileList)
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            {
                                while ((bytesReadFromStream = stream.Read(buffer, 0, buffer.Length)) > 0)
                                {
                                    token.ThrowIfCancellationRequested();
                                    totalBytesRead += bytesReadFromStream;

                                    if (stopwatch.Elapsed.TotalMilliseconds % 200 < 20)
                                    {
                                        var currentSpeed = totalBytesRead / stopwatch.Elapsed.TotalSeconds;
                                        this.Invoke((MethodInvoker)delegate { UpdateRealtimeStats(totalBytesRead, _totalFolderSize, currentSpeed); });
                                    }
                                }
                            }
                        }
                        catch (IOException ex) { Log($"Skipped file (I/O error): {Path.GetFileName(file)} - {ex.Message}"); }
                        catch (UnauthorizedAccessException) { Log($"Skipped file (access denied): {Path.GetFileName(file)}"); }
                    }
                }, token);

                stopwatch.Stop();
                UpdateRealtimeStats(_totalFolderSize, _totalFolderSize, 0);
                lblStatus.Text = "Status: Success!";
                Log($"Preload completed successfully. Total time: {stopwatch.Elapsed:hh\\:mm\\:ss}");
                MessageBox.Show($"Preload completed successfully!\n\nTotal Time: {stopwatch.Elapsed:hh\\:mm\\:ss}", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (OperationCanceledException)
            {
                lblStatus.Text = "Status: Canceled by user.";
                Log("Preload operation was canceled by user.");
                MessageBox.Show("Preload operation was canceled.", "Canceled", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                Log($"CRITICAL ERROR during preload: {ex.Message}");
                MessageBox.Show($"A critical error occurred: {ex.Message}", "Critical Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                lblStatus.Text = "Status: An error occurred.";
            }
            finally
            {
                _isPreloading = false;
                SetUIPreloadingState(false);
                _preloadCts.Dispose();
                _preloadCts = null;
            }
        }

        private void btnCancel_Click(object sender, EventArgs e)
        {
            if (_isCalculating)
            {
                Log("Cancel button clicked during calculation.");
                _calculationCts?.Cancel();
            }
            else if (_isPreloading)
            {
                Log("Cancel button clicked during preload.");
                _preloadCts?.Cancel();
            }
        }

        private void SetUICalculatingState(bool isCalculating)
        {
            btnPreload.Enabled = !isCalculating;
            btnBrowse.Enabled = !isCalculating;
            txtFolderPath.Enabled = !isCalculating;
            btnCancel.Enabled = isCalculating;
            btnCancel.Visible = isCalculating;
            trayIcon.Text = "Preloader - Calculating...";

            if (isCalculating)
            {
                lblStatus.Text = "Status: Calculating...";
                lblSize.Text = "Total Size: ...";
                lblProgress.Text = "...";
            }
        }

        private void SetUIPreloadingState(bool isPreloading)
        {
            btnPreload.Enabled = !isPreloading;
            btnBrowse.Enabled = !isPreloading;
            txtFolderPath.Enabled = !isPreloading;
            btnCancel.Enabled = isPreloading;
            btnCancel.Visible = isPreloading;

            if (!isPreloading)
            {
                UpdateStatusLabels(true);
                if (Directory.Exists(txtFolderPath.Text))
                    CalculateFolderSizeAsync(txtFolderPath.Text);
            }
        }

        private void UpdateRealtimeStats(long currentBytes, long totalBytes, double speedBps)
        {
            if (totalBytes == 0) return;
            int percentage = (int)((double)currentBytes * 100 / totalBytes);
            progressBar1.Value = percentage;
            lblProgress.Text = $"{percentage}%  |  {FormatBytes(currentBytes)} / {FormatBytes(totalBytes)}";
            lblSpeed.Text = $"Speed: {FormatBytes(speedBps)}/s";
            trayIcon.Text = $"Preloader - {percentage}%";

            if (speedBps > 0)
            {
                long remainingBytes = totalBytes - currentBytes;
                double remainingSeconds = remainingBytes / speedBps;
                lblETA.Text = $"ETA: {TimeSpan.FromSeconds(remainingSeconds):hh\\:mm\\:ss}";
            }
            else { lblETA.Text = "ETA: --:--:--"; }
        }

        private void UpdateStatusLabels(bool reset)
        {
            if (reset)
            {
                progressBar1.Value = 0;
                lblSize.Text = "Total Size: N/A";
                lblProgress.Text = "";
                lblSpeed.Text = "Speed: 0 MB/s";
                lblETA.Text = "ETA: --:--:--";
                lblStatus.Text = "Status: Select a folder to preload.";
                trayIcon.Text = "Preloader - Idle";
            }
        }

        private static string FormatBytes(double bytes)
        {
            string[] suffix = { "B", "KB", "MB", "GB", "TB", "PB" };
            int i;
            double dblSByte = bytes;
            for (i = 0; i < suffix.Length && bytes >= 1024; i++, bytes /= 1024)
            {
                dblSByte = bytes / 1024.0;
            }
            return $"{dblSByte:0.0} {suffix[i]}";
        }

        private void main_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effect = DragDropEffects.Copy; }
        }

        private string GetJsonValue(string json, string key)
        {
            string marker = $"\"{key}\":\"";
            int startIndex = json.IndexOf(marker) + marker.Length;
            int endIndex = json.IndexOf("\"", startIndex);
            if (startIndex < marker.Length || endIndex == -1)
            {
                throw new Exception($"Could not find key '{key}' in the GitHub API response.");
            }
            return json.Substring(startIndex, endIndex - startIndex);
        }

        private string GetDownloadUrl(string json)
        {
            string assetMarker = "\"browser_download_url\":\"";
            int currentIndex = 0;
            while ((currentIndex = json.IndexOf(assetMarker, currentIndex)) != -1)
            {
                currentIndex += assetMarker.Length;
                int endIndex = json.IndexOf("\"", currentIndex);
                string url = json.Substring(currentIndex, endIndex - currentIndex);
                if (url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    return url;
                }
            }
            return null;
        }
        #endregion

        private void main_Load_1(object sender, EventArgs e) {}
        private void trayMenu_Opening(object sender, System.ComponentModel.CancelEventArgs e) {}
    }
}
