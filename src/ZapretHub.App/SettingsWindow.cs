using ZapretHub.Core;

namespace ZapretHub.App;

internal sealed class SettingsWindow : Form
{
    private readonly NumericUpDown versions = new() { Minimum = 1, Maximum = 20, Width = 95 };
    private readonly NumericUpDown minutes = new() { Minimum = 1, Maximum = 1440, Width = 95 };
    private readonly TextBox control = new() { Dock = DockStyle.Fill };
    private readonly TextBox targets = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly ComboBox mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox profile = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox application = new() { Dock = DockStyle.Fill };
    private readonly CheckBox autoStart = new() { Text = "Запускать ZapretHub при входе в Windows", AutoSize = true };
    private readonly CheckBox autoRecover = new() { Text = "Автоматически подбирать конфиг при сбое", AutoSize = true };
    private readonly Label hint = new() { AutoSize = true, MaximumSize = new Size(590, 0), ForeColor = Color.DimGray };
    private readonly Label profileLabel = new() { Text = "Туннель WireGuard", Anchor = AnchorStyles.Left, AutoSize = true };
    private readonly Label applicationLabel = new() { Anchor = AnchorStyles.Left, AutoSize = true };

    public SettingsWindow(HubSettings current, IReadOnlyList<string> profiles, bool? startupEnabled)
    {
        Text = "Настройки ZapretHub";
        Font = new Font("Segoe UI", 10);
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(655, 480);
        MinimumSize = new Size(625, 480);
        ShowInTaskbar = false;
        MaximizeBox = false;
        MinimizeBox = false;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 2, RowCount = 12 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int row = 0; row < layout.RowCount; row++)
            layout.RowStyles.Add(row == 4 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));

        AddRow(layout, "Версий Zapret", versions, 0);
        AddRow(layout, "Проверка раз в, мин", minutes, 1);
        AddRow(layout, "Контрольный URL", control, 2);
        var targetLabel = new Label { Text = "URL сервисов, по одному на строку", AutoSize = true, Margin = new Padding(3, 13, 3, 3) };
        layout.Controls.Add(targetLabel, 0, 3);
        layout.SetColumnSpan(targetLabel, 2);
        layout.Controls.Add(targets, 0, 4);
        layout.SetColumnSpan(targets, 2);
        AddRow(layout, "Режим VPN", mode, 5);
        layout.Controls.Add(hint, 0, 6);
        layout.SetColumnSpan(hint, 2);
        layout.Controls.Add(profileLabel, 0, 7);
        layout.Controls.Add(profile, 1, 7);
        var appPicker = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        appPicker.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        appPicker.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var browse = new Button { Text = "Обзор…", AutoSize = true };
        appPicker.Controls.Add(application, 0, 0);
        appPicker.Controls.Add(browse, 1, 0);
        layout.Controls.Add(applicationLabel, 0, 8);
        layout.Controls.Add(appPicker, 1, 8);
        autoStart.Checked = startupEnabled == true;
        autoStart.Enabled = startupEnabled.HasValue;
        if (!startupEnabled.HasValue)
            autoStart.Text += " (Планировщик заданий недоступен)";
        layout.Controls.Add(autoStart, 0, 9);
        layout.SetColumnSpan(autoStart, 2);
        autoRecover.Checked = current.AutoRecover;
        layout.Controls.Add(autoRecover, 0, 10);
        layout.SetColumnSpan(autoRecover, 2);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
        var save = new Button { Text = "Сохранить", AutoSize = true };
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.AddRange([save, cancel]);
        layout.Controls.Add(buttons, 0, 11);
        layout.SetColumnSpan(buttons, 2);
        AcceptButton = save;
        CancelButton = cancel;
        Controls.Add(layout);

        versions.Value = current.KeepVersions;
        minutes.Value = current.MonitorMinutes;
        control.Text = current.ControlUrl.AbsoluteUri;
        targets.Lines = current.Targets.Select(url => url.AbsoluteUri).ToArray();
        application.Text = current.Vpn.Application ?? "";
        profile.Items.AddRange(profiles.Cast<object>().ToArray());
        if (current.Vpn.Profile is { } selected && profiles.Contains(selected))
            profile.SelectedItem = selected;
        mode.Items.AddRange(["Ручное подключение", "Автоматическое подключение"]);
        mode.SelectedIndexChanged += (_, _) => UpdateMode();
        mode.SelectedIndex = (int)current.Vpn.Mode;
        browse.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "Приложения Windows (*.exe)|*.exe", CheckFileExists = true };
            if (dialog.ShowDialog(this) == DialogResult.OK)
                application.Text = dialog.FileName;
        };
        save.Click += (_, _) => Save();
    }

    public HubSettings? Settings { get; private set; }
    public bool AutoStart => autoStart.Checked;

    private void UpdateMode()
    {
        bool automatic = mode.SelectedIndex == (int)VpnMode.Automatic;
        profile.Enabled = profileLabel.Enabled = automatic;
        applicationLabel.Text = automatic ? "Ручной резерв (необязательно)" : "Приложение VPN";
        hint.Text = automatic
            ? profile.Items.Count > 0
                ? "ZapretHub сам подключит выбранный туннель WireGuard. Служба туннеля должна быть установлена в Windows."
                : "Туннели WireGuard не найдены. Сначала настройте туннель как службу Windows."
            : "Для VPN-клиентов, где подключение нужно запускать вручную.";
    }

    private void Save()
    {
        try
        {
            string? path = string.IsNullOrWhiteSpace(application.Text) ? null : Path.GetFullPath(application.Text.Trim());
            if (path is not null && (!File.Exists(path) || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Выберите существующий EXE-файл VPN-клиента.");
            var result = new HubSettings
            {
                KeepVersions = (int)versions.Value,
                MonitorMinutes = (int)minutes.Value,
                AutoRecover = autoRecover.Checked,
                ControlUrl = new Uri(control.Text.Trim(), UriKind.Absolute),
                Targets = targets.Lines.Where(line => !string.IsNullOrWhiteSpace(line))
                    .Select(line => new Uri(line.Trim(), UriKind.Absolute)).ToArray(),
                Vpn = new VpnSettings((VpnMode)mode.SelectedIndex, path, profile.SelectedItem as string)
            };
            result.Validate();
            Settings = result;
            DialogResult = DialogResult.OK;
        }
        catch (Exception error) when (error is ArgumentException or UriFormatException or IOException)
        {
            MessageBox.Show(this, error.Message, "Проверьте настройки", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static void AddRow(TableLayoutPanel layout, string text, Control input, int row)
    {
        layout.Controls.Add(new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        layout.Controls.Add(input, 1, row);
    }
}
