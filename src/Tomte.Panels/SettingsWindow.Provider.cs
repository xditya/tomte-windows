using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Tomte.AI;
using Tomte.AI.Palette;
using Tomte.Core;

namespace Tomte.Panels;

/// <summary>
/// The AI provider section: which API, its address and key, the models and the daily cap. Test and save sends one
/// short request and keeps the key only when it works; Fetch models asks for the model list. Nothing here calls the
/// provider until one of those two buttons is clicked.
/// </summary>
public sealed partial class SettingsWindow
{
    // In ProviderKind's order.
    private static readonly string[] ProviderKinds = ["Anthropic", "Google Gemini", "OpenAI-compatible"];

    private readonly ComboBox providerKind = new() { ItemsSource = ProviderKinds, SelectedIndex = 0, Width = 280 };
    private readonly TextBox providerUrl = new() { PlaceholderText = "http://localhost:11434/v1", Width = 280 };
    private readonly PasswordBox providerKey = new() { Width = 280 };
    private readonly ComboBox defaultModel = new() { IsEditable = true, Width = 280 };
    private readonly ComboBox fastModel = new() { IsEditable = true, Width = 280 };
    private readonly NumberBox dailyCap = new()
    {
        Minimum = 0,
        Maximum = ProviderSetup.MaxDailyCapUsd,
        SmallChange = 0.5,
        PlaceholderText = "No cap",
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
        Width = 280,
    };

    private readonly Button providerSave = new() { Content = "Test and save" };
    private readonly Button providerModels = new() { Content = "Fetch models" };
    private readonly Button providerRemove = new() { Content = "Remove" };
    private readonly TextBlock providerNote = Note();
    private Grid? providerUrlRow;
    private bool providerKeySaved;
    private HttpClient? providerHttp;

    private Border ProviderSection()
    {
        providerUrlRow = Row("Server address", Note("OpenAI: https://api.openai.com/v1. OpenRouter: https://openrouter.ai/api/v1. Ollama: http://localhost:11434/v1. LM Studio: http://localhost:1234/v1."), providerUrl);
        var saved = store.Current.Provider;
        if (saved is not null)
        {
            providerKind.SelectedIndex = (int)saved.Kind;
            providerUrl.Text = saved.BaseUrl ?? "";
            ShowModel(defaultModel, saved.DefaultModel);
            ShowModel(fastModel, saved.FastModel);
            try
            {
                providerKeySaved = CredentialStore.Read(saved.Id) is not null;
            }
            catch (InvalidOperationException)
            {
                // Credential Manager would not answer; Test and save says so if the key is needed.
            }
        }

        var cap = saved is null ? new ProviderConfig("", ProviderKind.Anthropic).DailyCapUsd : saved.DailyCapUsd;
        dailyCap.Value = cap is { } dollars ? (double)dollars : double.NaN;
        var shownKind = (ProviderKind)providerKind.SelectedIndex;
        providerKind.SelectionChanged += (_, _) =>
        {
            // Another API's models do not apply; a selection of the kind already shown keeps them.
            if ((ProviderKind)providerKind.SelectedIndex == shownKind)
            {
                return;
            }

            shownKind = (ProviderKind)providerKind.SelectedIndex;
            defaultModel.ItemsSource = fastModel.ItemsSource = null;
            defaultModel.Text = fastModel.Text = "";
            ShowProvider();
        };
        providerUrl.TextChanged += (_, _) => ShowProvider();
        dailyCap.ValueChanged += (_, e) => SaveCap(e.NewValue);
        providerSave.Click += async (_, _) => await TestAndSaveProviderAsync();
        providerModels.Click += async (_, _) => await FetchModelsAsync();
        providerRemove.Click += (_, _) => RemoveProvider();
        ShowProvider();
        ProviderStatus(saved is null ? "No provider is set up, so the AI layer is off." : $"Set up: {saved.Id}, {saved.DefaultModel}.");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { providerSave, providerModels, providerRemove } };
        return Card(
            Note("The AI palette sends your requests to this provider. With none set up, the AI layer stays off and nothing is sent."),
            Row("Provider", null, providerKind),
            providerUrlRow,
            Row("Key", Note("Kept in Windows Credential Manager, not in the settings file, and saved only after a test call works. Leave it empty to keep the saved key. A server on this PC usually needs none."), providerKey),
            Row("Default model", Note("Pick one after Fetch models, or type its id. Left empty, Test and save uses the first model the provider lists."), defaultModel),
            Row("Fast model", Note("Optional. A cheaper model for the small calls: routing a voice command and reading a schedule into a job. Empty uses the default model."), fastModel),
            Row("Daily spend cap", Note($"US dollars a day for this provider, up to {ProviderSetup.MaxDailyCapUsd:0}. Empty means no cap. Spend is estimated from token counts and list prices as of {PriceTable.AsOf:d MMMM yyyy}; a cloud model Tomte has no price for counts at the highest one, and a server on this PC is free."), dailyCap),
            buttons,
            providerNote);
    }

    private void ShowProvider()
    {
        var form = FormProvider();
        providerUrlRow!.Visibility = form.Kind == ProviderKind.OpenAICompatible ? Visibility.Visible : Visibility.Collapsed;
        // The saved key goes only to the host it was saved for.
        bool same = store.Current.Provider is { } saved && string.Equals(saved.Id, form.Id, StringComparison.OrdinalIgnoreCase);
        providerKey.PlaceholderText = same && providerKeySaved ? "Saved" : form.Kind == ProviderKind.OpenAICompatible ? "None for a server on this PC" : "Paste the key";
        providerRemove.Visibility = store.Current.Provider is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private ProviderConfig FormProvider()
    {
        var kind = (ProviderKind)providerKind.SelectedIndex;
        string? url = kind == ProviderKind.OpenAICompatible ? providerUrl.Text.Trim() : null;
        return new ProviderConfig(ProviderSetup.IdFor(kind, url), kind, url, ModelId(defaultModel), ModelId(fastModel), ProviderSetup.CapFrom(dailyCap.Value));
    }

    // The saved model as the box's one item, picked: text alone, set on an editable box before the window first
    // showed, came up empty, while a picked item is what the box shows as its text.
    private static void ShowModel(ComboBox box, string? id)
    {
        if (id is { Length: > 0 })
        {
            box.ItemsSource = new[] { id };
            box.SelectedIndex = 0;
            box.Text = id;
        }
    }

    // Typed or picked: the box's text, or the item picked before anything was typed.
    private static string? ModelId(ComboBox box) =>
        (string.IsNullOrWhiteSpace(box.Text) ? box.SelectedItem as string : box.Text)?.Trim() is { Length: > 0 } id ? id : null;

    private async Task TestAndSaveProviderAsync()
    {
        var saved = store.Current.Provider;
        var config = FormProvider();
        if (!CheckProvider(saved, config, out var key))
        {
            return;
        }

        Busy(true, "Sending one short test request...");
        try
        {
            using var limit = new CancellationTokenSource(ProviderSetup.TestLimit);
            var http = providerHttp ??= ProviderSetup.NewHttp();
            var tested = await Task.Run(() => ProviderSetup.TestAndSaveAsync(saved, config, key, http, CredentialStore.Save, CredentialStore.Delete, limit.Token));
            Save(s => s with { Provider = tested });
            providerKeySaved = key is not null;
            providerKey.Password = "";
            defaultModel.Text = tested.DefaultModel;
            ShowProvider();
            string listed;
            try
            {
                listed = await ListModelsAsync(tested, key);
            }
            catch (Exception e)
            {
                listed = "The model list could not be fetched: " + ProviderSetup.Explain(e, key);
            }

            ProviderStatus($"Saved. The test call to {tested.DefaultModel} worked. {listed}");
        }
        catch (Exception e)
        {
            // Called from an async void handler: an exception leaving here would end the settings process.
            Show(providerNote, ProviderSetup.Explain(e, key));
        }
        finally
        {
            Busy(false, null);
        }
    }

    private async Task FetchModelsAsync()
    {
        var config = FormProvider();
        if (!CheckProvider(store.Current.Provider, config, out var key))
        {
            return;
        }

        Busy(true, "Asking the provider for its models...");
        try
        {
            ProviderStatus(await ListModelsAsync(config, key));
        }
        catch (Exception e)
        {
            Show(providerNote, ProviderSetup.Explain(e, key));
        }
        finally
        {
            Busy(false, null);
        }
    }

    // The form check and the key to use; a refusal shows under the buttons.
    private bool CheckProvider(ProviderConfig? saved, ProviderConfig config, out string? key)
    {
        string? error;
        try
        {
            error = ProviderSetup.Check(saved, config, providerKey.Password, CredentialStore.Read, out key);
        }
        catch (InvalidOperationException e)
        {
            // Credential Manager would not read the saved key.
            (error, key) = (e.Message, null);
        }

        Show(providerNote, error);
        return error is null;
    }

    // Off the window's thread, with the same limit as the test call. Fills both model lists and says what came back.
    private async Task<string> ListModelsAsync(ProviderConfig config, string? key)
    {
        using var limit = new CancellationTokenSource(ProviderSetup.TestLimit);
        var http = providerHttp ??= ProviderSetup.NewHttp();
        var models = await Task.Run(() => ProviderSetup.Create(config, http, key).ListModelsAsync(limit.Token));
        string[] ids = [.. models.Select(m => m.Id)];
        foreach (var box in new[] { defaultModel, fastModel })
        {
            // A new list would clear what the box shows.
            string text = box.Text;
            box.ItemsSource = ids;
            box.Text = text;
        }

        return ids.Length == 0 ? "This server lists no models. Type the model's id." : $"{ids.Length} models listed. Pick one or type an id.";
    }

    private void RemoveProvider()
    {
        if (store.Current.Provider is not { } saved)
        {
            return;
        }

        string? left = ProviderSetup.Remove(saved, CredentialStore.Delete);
        Save(s => s with { Provider = null });
        bool hadKey = providerKeySaved;
        providerKeySaved = false;
        providerKey.Password = "";
        ShowProvider();
        if (left is not null)
        {
            Show(providerNote, left);
        }
        else
        {
            ProviderStatus(hadKey ? "Removed, and its key deleted. The AI layer is off." : "Removed. The AI layer is off.");
        }
    }

    // The cap needs no test call: it changes at once for the saved provider, and goes with the next Test and save otherwise.
    private void SaveCap(double value)
    {
        if (store.Current.Provider is { } saved && saved.DailyCapUsd != ProviderSetup.CapFrom(value))
        {
            Save(s => s with { Provider = saved with { DailyCapUsd = ProviderSetup.CapFrom(value) } });
        }
    }

    private void Busy(bool on, string? status)
    {
        // The whole form, so nothing typed during the call is lost to what the call saves.
        foreach (var control in new Control[] { providerKind, providerUrl, providerKey, defaultModel, fastModel, dailyCap, providerSave, providerModels, providerRemove })
        {
            control.IsEnabled = !on;
        }

        if (status is not null)
        {
            ProviderStatus(status);
        }
    }

    private void ProviderStatus(string text)
    {
        providerNote.Text = text;
        providerNote.Visibility = Visibility.Visible;
        providerNote.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    }
}
