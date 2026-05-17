namespace TahoeTitlebarOneClick;

public partial class Form1 : Form
{
    private readonly Button applyButton;
    private readonly Button restoreButton;
    private readonly ProgressBar progressBar;
    private readonly TextBox logBox;

    public Form1()
    {
        Text = "Jhon-Lloyd Molino Tahoe Titlebar";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 540);
        Size = new Size(840, 620);
        BackColor = Color.FromArgb(18, 20, 24);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10f);

        var title = new Label
        {
            AutoSize = false,
            Text = "Jhon-Lloyd Molino",
            Dock = DockStyle.Top,
            Height = 58,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI Semibold", 24f, FontStyle.Bold),
            ForeColor = Color.White
        };

        var subtitle = new Label
        {
            AutoSize = false,
            Text = "close/minimize/maximize + translucent Tahoe taskbar",
            Dock = DockStyle.Top,
            Height = 34,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 13f),
            ForeColor = Color.FromArgb(210, 220, 230)
        };

        applyButton = new Button
        {
            Text = "Fix Everything Automatically",
            Height = 52,
            Dock = DockStyle.Top,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(34, 197, 94),
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 13f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        applyButton.FlatAppearance.BorderSize = 0;
        applyButton.Click += ApplyButton_Click;

        restoreButton = new Button
        {
            Text = "Old Windows close/minimize/maximize + taskbar",
            Height = 46,
            Dock = DockStyle.Top,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(52, 58, 68),
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 11f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        restoreButton.FlatAppearance.BorderColor = Color.FromArgb(80, 88, 100);
        restoreButton.Click += RestoreButton_Click;

        progressBar = new ProgressBar
        {
            Dock = DockStyle.Top,
            Height = 12,
            Style = ProgressBarStyle.Continuous
        };

        logBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(10, 12, 16),
            ForeColor = Color.FromArgb(220, 235, 245),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 9.5f)
        };

        var note = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 54,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(170, 178, 188),
            Text = "Runs diagnosis, applies every safe supported change, verifies what worked, and opens a final report."
        };

        var body = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18)
        };
        body.Controls.Add(logBox);
        body.Controls.Add(progressBar);
        body.Controls.Add(restoreButton);
        body.Controls.Add(applyButton);
        body.Controls.Add(note);

        Controls.Add(body);
        Controls.Add(subtitle);
        Controls.Add(title);

        AppendLog("Ready. Choose Fix Everything Automatically, or restore old Windows buttons/taskbar settings.");
    }

    private async void ApplyButton_Click(object? sender, EventArgs e)
    {
        await RunBusyAsync("Running Tahoe auto diagnose/configure/install/verify...", async installer =>
        {
            var report = await installer.FixEverythingAutomatically();
            if (report.RebootRequired)
            {
                BeginInvoke(() => MessageBox.Show(
                    this,
                    "A reboot is required to finish the Settings/UWP titlebar patch safely.",
                    "Tahoe reboot required",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information));
            }
        });
    }

    private async void RestoreButton_Click(object? sender, EventArgs e)
    {
        await RunBusyAsync("Restoring old Windows titlebar/taskbar settings from latest backup...", installer => installer.RestoreLatestBackup());
    }

    private async Task RunBusyAsync(string firstLine, Func<TahoeInstaller, Task> action)
    {
        applyButton.Enabled = false;
        restoreButton.Enabled = false;
        progressBar.Value = 0;
        AppendLog("");
        AppendLog(firstLine);

        var installer = new TahoeInstaller(AppendLog, SetProgress);
        try
        {
            await action(installer);
            SetProgress(100);
            AppendLog("Operation completed. Read the final report above for Full / Partial / Failed status.");
        }
        catch (Exception ex)
        {
            AppendLog("FAILED: " + ex.Message);
            AppendLog(ex.ToString());
            MessageBox.Show(this, ex.Message, "Tahoe installer failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            applyButton.Enabled = true;
            restoreButton.Enabled = true;
        }
    }

    private void SetProgress(int value)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => SetProgress(value));
            return;
        }
        progressBar.Value = Math.Max(0, Math.Min(100, value));
    }

    private void AppendLog(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(message));
            return;
        }
        logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }
}
