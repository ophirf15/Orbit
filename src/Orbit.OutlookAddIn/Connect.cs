using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Extensibility;
using Office;

namespace Orbit.OutlookAddIn;

[ComVisible(true)]
[Guid("B7E6C2A1-4F3D-4E8A-9C1B-0D2E3F4A5B6C")]
[ProgId("Orbit.OutlookAddIn.Connect")]
[ClassInterface(ClassInterfaceType.AutoDispatch)]
[ComDefaultInterface(typeof(IDTExtensibility2))]
public sealed class Connect : IDTExtensibility2, IRibbonExtensibility
{
    private object? _outlook;

    public void OnConnection(
        object Application,
        ext_ConnectMode ConnectMode,
        object AddInInst,
        ref Array custom)
    {
        try
        {
            _outlook = Application;
            AddInLog.Write("OnConnection ok mode=" + ConnectMode);
            try
            {
                File.WriteAllText(
                    Path.Combine(Path.GetTempPath(), "orbit-outlook-addin-alive.txt"),
                    DateTime.Now.ToString("O") + " mode=" + ConnectMode);
            }
            catch
            {
                // ignore
            }
        }
        catch (Exception ex)
        {
            AddInLog.Write("OnConnection failed: " + ex);
        }
    }

    public void OnDisconnection(ext_DisconnectMode RemoveMode, ref Array custom)
    {
        _outlook = null;
        AddInLog.Write("OnDisconnection");
    }

    public void OnAddInsUpdate(ref Array custom)
    {
    }

    public void OnStartupComplete(ref Array custom)
    {
        AddInLog.Write("OnStartupComplete");
    }

    public void OnBeginShutdown(ref Array custom)
    {
        _outlook = null;
    }

    public string GetCustomUI(string RibbonID)
    {
        try
        {
            AddInLog.Write("GetCustomUI ribbonId=" + RibbonID);
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("Orbit.OutlookAddIn.Ribbon.xml");
            if (stream is null)
            {
                return string.Empty;
            }

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            AddInLog.Write("GetCustomUI failed: " + ex);
            return string.Empty;
        }
    }

    public void Ribbon_Load(object ribbonUi) => AddInLog.Write("Ribbon_Load");

    public void OnAddToOrbit(object control) => PushSelected(projectIds: null);

    public void OnAddToProject(object control)
    {
        try
        {
            var config = OrbitConfig.Load();
            using var client = new OrbitIngestClient(config);
            if (!client.TryHealthAsync().GetAwaiter().GetResult())
            {
                MessageBox.Show(
                    "Orbit Core Host is not reachable.\n\nStart Orbit, then try again.\n"
                    + (client.LastError ?? string.Empty),
                    "Orbit",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            var projects = client.ListProjectsAsync().GetAwaiter().GetResult();
            if (projects.Count == 0)
            {
                MessageBox.Show(
                    "No Orbit projects found (or Core rejected the request).\n"
                    + (client.LastError ?? string.Empty),
                    "Orbit",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            using var picker = new ProjectPickerForm(projects);
            if (picker.ShowDialog() != DialogResult.OK || string.IsNullOrWhiteSpace(picker.SelectedProjectId))
            {
                return;
            }

            PushSelected(new List<string> { picker.SelectedProjectId! });
        }
        catch (Exception ex)
        {
            AddInLog.Write("OnAddToProject: " + ex);
            MessageBox.Show(ex.Message, "Orbit", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void PushSelected(IReadOnlyList<string>? projectIds)
    {
        try
        {
            if (_outlook is null)
            {
                MessageBox.Show("Outlook application is not available.", "Orbit");
                return;
            }

            var mails = MailSelection.GetSelectedMailItems(_outlook);
            if (mails.Count == 0)
            {
                MessageBox.Show(
                    "Select one or more mail messages, then click Add to Orbit.",
                    "Orbit",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var config = OrbitConfig.Load();
            if (!config.HasApiKey)
            {
                var proceed = MessageBox.Show(
                    "No Core Host API key found under %LocalAppData%\\Orbit.\nContinue anyway?",
                    "Orbit",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (proceed != DialogResult.Yes)
                {
                    return;
                }
            }

            using var client = new OrbitIngestClient(config);
            if (!client.TryHealthAsync().GetAwaiter().GetResult())
            {
                MessageBox.Show(
                    "Orbit Core Host is not reachable at " + config.CoreBaseUrl
                    + "\n\n" + (client.LastError ?? string.Empty),
                    "Orbit",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            var ok = 0;
            var fail = 0;
            var lines = new List<string>();
            foreach (var mail in mails)
            {
                string? path = null;
                try
                {
                    path = MailExport.SaveMsgTemp(mail);
                    var result = client.IngestMsgFileAsync(path, projectIds).GetAwaiter().GetResult();
                    if (!result.HasValue)
                    {
                        fail++;
                        lines.Add("FAIL: " + (MailExport.SafeSubject(mail) ?? "(no subject)") + " — " + client.LastError);
                    }
                    else
                    {
                        var ingested = result.Value;
                        ok++;
                        var tag = ingested.WasExisting ? "updated" : "added";
                        lines.Add($"{tag}: {ingested.Subject ?? MailExport.SafeSubject(mail) ?? ingested.Id}");
                    }
                }
                catch (Exception ex)
                {
                    fail++;
                    lines.Add("FAIL: " + ex.Message);
                    AddInLog.Write("Push mail failed: " + ex);
                }
                finally
                {
                    try
                    {
                        if (path is not null && File.Exists(path))
                        {
                            File.Delete(path);
                        }
                    }
                    catch
                    {
                        // ignore
                    }

                    try
                    {
                        Marshal.ReleaseComObject(mail);
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }

            MessageBox.Show(
                $"{ok} pushed, {fail} failed.\n\n" + string.Join("\n", lines.Take(12)),
                "Orbit",
                MessageBoxButtons.OK,
                fail > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            AddInLog.Write("PushSelected: " + ex);
            MessageBox.Show(ex.Message, "Orbit", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
