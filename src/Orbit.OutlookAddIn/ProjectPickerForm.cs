using System.Windows.Forms;

namespace Orbit.OutlookAddIn;

internal sealed class ProjectPickerForm : Form
{
    private readonly ListBox _list;
    private readonly Button _ok;
    private readonly Button _cancel;

    public string? SelectedProjectId { get; private set; }

    public ProjectPickerForm(IReadOnlyList<OrbitIngestClient.ProjectItem> projects)
    {
        Text = "Add to Orbit project";
        Width = 420;
        Height = 360;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        _list = new ListBox
        {
            Dock = DockStyle.Fill,
            DisplayMember = nameof(OrbitIngestClient.ProjectItem.Name),
            ValueMember = nameof(OrbitIngestClient.ProjectItem.Id),
        };
        foreach (var p in projects)
        {
            _list.Items.Add(p);
        }

        if (_list.Items.Count > 0)
        {
            _list.SelectedIndex = 0;
        }

        _ok = new Button { Text = "Add", DialogResult = DialogResult.OK, Width = 100 };
        _cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 100 };
        _ok.Click += (_, _) =>
        {
            if (_list.SelectedItem is OrbitIngestClient.ProjectItem item)
            {
                SelectedProjectId = item.Id;
                DialogResult = DialogResult.OK;
                Close();
            }
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8),
        };
        buttons.Controls.Add(_ok);
        buttons.Controls.Add(_cancel);

        Controls.Add(_list);
        Controls.Add(buttons);
        AcceptButton = _ok;
        CancelButton = _cancel;
    }
}
