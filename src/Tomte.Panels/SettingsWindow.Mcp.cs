using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Tomte.AI;
using Tomte.AI.Mcp;
using Tomte.Core;
using Tomte.Core.Device;

namespace Tomte.Panels;

/// <summary>
/// The MCP servers section: add, edit, turn on or off and remove servers, and list a server's
/// tools with the tier each one runs under. Listing starts the server for a moment and stops it again.
/// </summary>
public sealed partial class SettingsWindow
{
    private static readonly string[] Kinds = ["Command (stdio)", "URL (Streamable HTTP)"];

    private readonly StackPanel mcpList = new() { Spacing = 12 };
    private readonly TextBox mcpName = new() { PlaceholderText = "Files", Width = 280 };
    private readonly ComboBox mcpKind = new() { ItemsSource = Kinds, SelectedIndex = 0, Width = 280 };
    private readonly TextBox mcpTarget = new() { Width = 280 };
    private readonly TextBox mcpArguments = new() { PlaceholderText = "-y @modelcontextprotocol/server-filesystem C:\\Notes", Width = 280 };
    private readonly TextBox mcpHeaderName = new() { PlaceholderText = "Authorization", Width = 280 };
    private readonly PasswordBox mcpHeaderValue = new() { Width = 280 };
    private readonly Button mcpSave = new() { Content = "Add server" };
    private readonly Button mcpCancel = new() { Content = "Cancel", Visibility = Visibility.Collapsed };
    private readonly TextBlock mcpNote = Note();
    private Grid? mcpArgumentsRow, mcpHeaderNameRow, mcpHeaderValueRow;
    private string? mcpEditing;

    private Border McpSection()
    {
        mcpArgumentsRow = Row("Arguments", Note("Separated by spaces. Put an argument with spaces in double quotes. Arguments are stored as plain text in the settings file and shown in this window."), mcpArguments);
        mcpHeaderNameRow = Row("Header name", Note("Optional. Sent with every request, for example Authorization. Needs an https URL."), mcpHeaderName);
        mcpHeaderValueRow = Row("Header value", Note("Kept in Windows Credential Manager, not in the settings file. Leave empty to keep the saved value. Enter it again after changing the URL."), mcpHeaderValue);
        mcpKind.SelectionChanged += (_, _) => ShowMcpKind();
        mcpSave.Click += (_, _) => SaveMcpServer();
        mcpCancel.Click += (_, _) => ResetMcpForm();
        ShowMcpKind();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { mcpSave, mcpCancel } };
        return Card(
            Note("Tools from these servers are offered to the AI palette. A server starts when you send a request and stops when the palette closes. Each tool asks before it runs unless you mark it otherwise below."),
            mcpList,
            Row("Name", null, mcpName),
            Row("Kind", null, mcpKind),
            Row("Command or URL", Note("For a command, the program to run, for example npx or uvx."), mcpTarget),
            mcpArgumentsRow,
            mcpHeaderNameRow,
            mcpHeaderValueRow,
            buttons,
            mcpNote);
    }

    private bool McpStdio => mcpKind.SelectedIndex == 0;

    private void ShowMcpKind()
    {
        mcpTarget.PlaceholderText = McpStdio ? "npx" : "https://example.com/mcp";
        mcpArgumentsRow!.Visibility = McpStdio ? Visibility.Visible : Visibility.Collapsed;
        mcpHeaderNameRow!.Visibility = mcpHeaderValueRow!.Visibility = McpStdio ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RenderMcpServers()
    {
        mcpList.Children.Clear();
        if (store.Current.McpServers.Count == 0)
        {
            mcpList.Children.Add(Note("No servers yet."));
        }

        foreach (var server in store.Current.McpServers)
        {
            var enabled = new ToggleSwitch { IsOn = server.Enabled, OnContent = "On", OffContent = "Off" };
            enabled.Toggled += (_, _) => SaveMcp(s => s.Name == server.Name ? s with { Enabled = enabled.IsOn } : s);
            var tools = new StackPanel { Spacing = 8, Margin = new Thickness(16, 0, 0, 0) };
            var list = new Button { Content = "Tools" };
            list.Click += async (_, _) => await ShowMcpToolsAsync(server.Name, tools, list);
            var edit = new Button { Content = "Edit" };
            edit.Click += (_, _) => EditMcpServer(server);
            var remove = new Button { Content = "Remove" };
            remove.Click += (_, _) =>
            {
                TryCredential(() => CredentialStore.Delete(server.CredentialId));
                // The server's always-allow grants go with it, so adding a server of the same name later starts from asking.
                Save(s => McpTools.WithServer(s, server, null));
                RenderMcpServers();
                RenderGrants();
            };
            var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { enabled, list, edit, remove } };
            mcpList.Children.Add(new StackPanel { Spacing = 8, Children = { Row(server.Name, Note(server.Summary), controls), tools } });
        }
    }

    private async Task ShowMcpToolsAsync(string name, StackPanel panel, Button button)
    {
        var server = store.Current.McpServers.FirstOrDefault(s => s.Name == name);
        if (server is null)
        {
            return;
        }

        button.IsEnabled = false;
        panel.Children.Clear();
        panel.Children.Add(Note("Starting the server to ask for its tools..."));
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var tools = await McpTools.ProbeAsync(server, timeout.Token);
            panel.Children.Clear();
            if (tools.Count == 0)
            {
                panel.Children.Add(Note("This server has no tools."));
            }

            foreach (var tool in tools)
            {
                var tiers = server.AllowedTiers;
                var box = new ComboBox { ItemsSource = tiers.Select(TierName).ToArray(), SelectedIndex = IndexOf(tiers, server.TierFor(tool.Key)), MinWidth = 200 };
                box.SelectionChanged += (_, _) => SaveMcp(s => s.Name == name ? s.WithTier(tool.Key, tiers[box.SelectedIndex]) : s);
                panel.Children.Add(Row(tool.Name, tool.Description.Length == 0 ? null : Note(FirstSentence(tool.Description)), box));
            }

            // A tier or an always-allow for a tool the server no longer has, or has since changed, applies to
            // nothing. A Destructive mark is the exception and stays: a changed tool keeps it by name.
            var keys = tools.Select(t => t.Key).ToHashSet();
            bool Stale(string key) => server.OwnsKey(key) && !keys.Contains(key);
            if (server.WithTiersFor(keys).ToolTiers.Count != server.ToolTiers.Count || store.Current.AlwaysAllowedTools.Any(Stale))
            {
                Save(s => s with
                {
                    McpServers = [.. s.McpServers.Select(o => o.Name == name ? o.WithTiersFor(keys) : o)],
                    AlwaysAllowedTools = [.. s.AlwaysAllowedTools.Where(g => !Stale(g))],
                });
                RenderGrants();
            }
        }
        catch (Exception e)
        {
            // Called from an async void handler: an exception leaving here would end the settings process.
            // McpTools already hid the header value in its message and cut it to a fixed length.
            panel.Children.Clear();
            var note = Note();
            Show(note, e is OperationCanceledException ? "Could not list the tools: the server did not answer within 60 seconds." : $"Could not list the tools: {e.Message}");
            panel.Children.Add(note);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    // Server descriptions are written for the model and run long; the first sentence is enough to recognise a tool.
    // A description with no sentence end is cut instead, so one row never fills the window.
    private static string FirstSentence(string text)
    {
        int end = text.IndexOf(". ", StringComparison.Ordinal);
        string first = end > 0 ? text[..(end + 1)] : text;
        return first.Length > 160 ? first[..157] + "..." : first;
    }

    private static string TierName(ActionTier tier) => tier switch
    {
        ActionTier.Read => "Read: runs without asking",
        ActionTier.Destructive => "Destructive: asks every time",
        _ => "External effect: asks, can be always allowed",
    };

    private static int IndexOf(IReadOnlyList<ActionTier> tiers, ActionTier tier) => Math.Max(0, tiers.ToList().IndexOf(tier));

    private void EditMcpServer(McpServerSettings server)
    {
        mcpEditing = server.Name;
        mcpName.Text = server.Name;
        mcpKind.SelectedIndex = server.Transport == McpTransport.Stdio ? 0 : 1;
        mcpTarget.Text = server.Transport == McpTransport.Stdio ? server.Command : server.Url;
        mcpArguments.Text = McpServerSettings.JoinArguments(server.Arguments);
        mcpHeaderName.Text = server.HeaderName;
        mcpHeaderValue.Password = "";
        mcpSave.Content = "Save changes";
        mcpCancel.Visibility = Visibility.Visible;
        Show(mcpNote, null);
    }

    private void ResetMcpForm()
    {
        mcpEditing = null;
        mcpName.Text = mcpTarget.Text = mcpArguments.Text = mcpHeaderName.Text = "";
        mcpHeaderValue.Password = "";
        mcpSave.Content = "Add server";
        mcpCancel.Visibility = Visibility.Collapsed;
        Show(mcpNote, null);
    }

    private void SaveMcpServer()
    {
        var existing = store.Current.McpServers.FirstOrDefault(s => s.Name == mcpEditing);
        var server = new McpServerSettings
        {
            Name = mcpName.Text.Trim(),
            Transport = McpStdio ? McpTransport.Stdio : McpTransport.Http,
            Command = McpStdio ? mcpTarget.Text.Trim() : "",
            Arguments = McpStdio ? McpServerSettings.SplitArguments(mcpArguments.Text) : [],
            Url = McpStdio ? "" : mcpTarget.Text.Trim(),
            HeaderName = McpStdio ? "" : mcpHeaderName.Text.Trim(),
        }.After(existing);
        string? error = McpServerSettings.Validate(store.Current.McpServers.Where(s => s.Name != mcpEditing), server);
        // An empty box keeps the saved value, and a rename moves it to the new name. A changed URL does
        // not keep it: the value was entered for the old address.
        string? secret = mcpHeaderValue.Password.Length > 0 ? mcpHeaderValue.Password : null;
        if (error is null && secret is null && existing is not null && existing.Target == server.Target
            && !TryCredential(() => secret = CredentialStore.Read(existing.CredentialId)))
        {
            return;
        }

        if (error is null && server.HeaderName.Length > 0 && secret is null)
        {
            error = "Enter the header value.";
        }

        Show(mcpNote, error);
        if (error is not null || !TryCredential(() =>
            {
                if (existing is not null && existing.CredentialId != server.CredentialId)
                    CredentialStore.Delete(existing.CredentialId);
                if (server.HeaderName.Length > 0)
                    CredentialStore.Save(server.CredentialId, secret!);
                else
                    CredentialStore.Delete(server.CredentialId);
            }))
        {
            return;
        }

        // A renamed server, or one pointed at another command or URL, loses its always-allow grants.
        Save(s => McpTools.WithServer(s, existing, server));
        ResetMcpForm();
        RenderMcpServers();
        RenderGrants();
    }

    private void SaveMcp(Func<McpServerSettings, McpServerSettings> change) =>
        Save(s => s with { McpServers = [.. s.McpServers.Select(change)] });

    private bool TryCredential(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (InvalidOperationException e)
        {
            Show(mcpNote, e.Message);
            return false;
        }
    }
}
