using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection; // This can be removed if not used elsewhere, but it's safe to keep.
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Preload
{
    public partial class main : Form
    {
        private const string _githubRepo = "Allesanddro/Preload";

        // --- [CHANGE] --- Using a hardcoded string for the version number.
        // Manually update this string when you create a new release.
        private const string _currentVersionString = "1.0.1";

        private CancellationTokenSource _preloadCts;
        private CancellationTokenSource _calculationCts;
        private string[] _fileList;
        private long _totalFolderSize;
        private bool _isCalculating;

        public main()
        {
            InitializeComponent();

            // --- [CHANGE] --- Removed assembly reading, now uses the hardcoded string.
            this.Text = $"PrimoCache Preloader v{_currentVersionString}";

            this.Load += main_Load;
            this.AllowDrop = true;
            this.DragEnter += main_DragEnter;
            this.DragDrop += main_DragDrop;
        }

        private void main_Load(object sender, EventArgs e)
        {
            Log("Application started.");
            UpdateStatusLabels(true);
            string lastPath = Properties.Settings.Default.LastFolderPath;
            if (!string.IsNullOrEmpty(lastPath) && Directory.Exists(lastPath))
            {
                txtFolderPath.Text = lastPath;
                Log($"Loaded last used folder: {lastPath}");
                CalculateFolderSizeAsync(lastPath);
            }
        }

        private async void btnUpdateCheck_Click(object sender, EventArgs e)
        {
            Log("Checking for updates...");
            btnUpdateCheck.Enabled = false;
            btnUpdateCheck.Text = "Checking...";

            string url = $"https://api.github.com/repos/{_githubRepo}/releases/latest";

            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "PrimoCache-Preloader-Update-Check");
                    string json = await client.GetStringAsync(url);

                    string tagNameMarker = "\"tag_name\":\"";
                    int startIndex = json.IndexOf(tagNameMarker) + tagNameMarker.Length;
                    int endIndex = json.IndexOf("\"", startIndex);
                    string latestTag = json.Substring(startIndex, endIndex - startIndex);

                    Version latestVersion = new Version(latestTag.TrimStart('v'));

                    // --- [CHANGE] --- Create a Version object from our hardcoded string for comparison.
                    Version currentVersion = new Version(_currentVersionString);

                    Log($"Current version: {currentVersion}. Latest version on GitHub: {latestVersion}");

                    if (latestVersion > currentVersion)
                    {
                        var result = MessageBox.Show(
                            $"A new version ({latestTag}) is available!\n\nYou are currently using version {currentVersion}.\n\nWould you like to go to the download page now?",
                            "Update Available",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Information);

                        if (result == DialogResult.Yes)
                        {
                            Log("User clicked Yes to download update.");
                            Process.Start($"https://github.com/{_githubRepo}/releases/latest");
                        }
                    }
                    else
                    {
                        MessageBox.Show("You are running the latest version.", "Up to Date", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"ERROR: Could not check for updates. {ex.Message}");
                MessageBox.Show($"Could not check for updates.\nPlease check your internet connection or visit the GitHub page manually.\n\nError: {ex.Message}", "Update Check Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnUpdateCheck.Enabled = true;
                btnUpdateCheck.Text = "Check for Updates...";
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
            Properties.Settings.Default.LastFolderPath = txtFolderPath.Text;
            Properties.Settings.Default.Save();

            _preloadCts = new CancellationTokenSource();
            var token = _preloadCts.Token;

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
            else
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
        #endregion
    }
}
