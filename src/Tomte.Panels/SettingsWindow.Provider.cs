using System.Text.Json;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Tomte.AI;
using Tomte.AI.Palette;
using Tomte.Core;
using Colors = Microsoft.UI.Colors;

namespace Tomte.Panels;

/// <summary>
/// The Provider and spend page: which API as three cards, its address and key, the models, the daily cap beside what was
/// spent today, and the AI history. Test and save sends one short request and keeps the key only when it works; Fetch
/// models asks for the model list. Nothing here calls the provider until one of those two buttons is clicked.
/// </summary>
public sealed partial class SettingsWindow
{
    private const string ProviderPage = "Provider and spend";

    // In ProviderKind's order: the name on the card and the line under it.
    private static readonly (string Name, string Where)[] ProviderKinds =
    [
        ("Anthropic", "api.anthropic.com"),
        ("Gemini", "Google AI Studio key"),
        ("OpenAI-compatible", "Any server, including Ollama on this PC"),
    ];

    private readonly List<(RadioButton Radio, Border Card, UIElement Connected)> providerCards = [];
    private readonly TextBox providerUrl = new() { PlaceholderText = "http://localhost:11434/v1" };
    private readonly PasswordBox providerKey = new();
    private readonly TextBlock providerKeyMask = new() { FontFamily = new FontFamily(Palette.Mono), FontSize = 13, Foreground = Palette.Brush(Palette.Ink2), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button providerKeyReplace = new() { Content = "Replace", Height = 28, Padding = new Thickness(10, 0, 10, 0), CornerRadius = new CornerRadius(7), FontSize = 12 };
    private readonly Border providerKeySaved = new()
    {
        Width = 300,
        Height = 38,
        CornerRadius = new CornerRadius(10),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(12, 0, 5, 0),
        Background = Palette.Brush(Palette.Field),
        BorderBrush = Palette.Brush(Palette.LineStrong),
    };

    private readonly ComboBox defaultModel = new() { IsEditable = true, PlaceholderText = "Pick or type a model id" };
    private readonly ComboBox fastModel = new() { IsEditable = true, PlaceholderText = "Same as default" };
    private readonly NumberBox dailyCap = new()
    {
        Minimum = 0,
        Maximum = ProviderSetup.MaxDailyCapUsd,
        SmallChange = 0.5,
        PlaceholderText = "No cap",
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
        ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
        NumberFormatter = new Windows.Globalization.NumberFormatting.DecimalFormatter { FractionDigits = 2, IntegerDigits = 1 },
        Width = 96,
        FontFamily = new FontFamily(Palette.Mono),
    };

    private readonly TextBlock spentToday = new() { FontFamily = new FontFamily(Palette.Mono), FontSize = 28, FontWeight = FontWeights.SemiBold, CharacterSpacing = -20, Foreground = Palette.Brush(Palette.Ink), Text = "$0.00" };
    private readonly Grid spendMeter = new() { Height = 6, CornerRadius = new CornerRadius(3), Background = Palette.Brush(Palette.Outline) };
    private readonly Border spendFill = new() { CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Background = Palette.AccentBrush() };
    private readonly SolidColorBrush providerPicked = Palette.AccentBrush();
    private readonly Button providerSave = new() { Content = "Test and save" };
    private readonly Button providerModels = new() { Content = "Fetch models" };
    private readonly Button providerRemove = new() { Content = "Remove provider" };
    private readonly TextBlock providerNote = Note();
    private Grid? providerUrlRow;
    private ProviderKind providerShown;
    private bool providerKeyIsSaved, providerKeyReplacing;
    private decimal spent;
    private HttpClient? providerHttp;

    private string SendLogPath => Path.Combine(DataFolder, "send-log.jsonl");

    private UIElement[] ProviderSection()
    {
        var saved = store.Current.Provider;
        if (saved is not null)
        {
            providerShown = saved.Kind;
            providerUrl.Text = saved.BaseUrl ?? "";
            ShowModel(defaultModel, saved.DefaultModel);
            ShowModel(fastModel, saved.FastModel);
            try
            {
                // Only the masked form is kept; the key itself is dropped at once.
                string? key = CredentialStore.Read(saved.Id);
                providerKeyIsSaved = key is not null;
                providerKeyMask.Text = key is null ? "" : ProviderSetup.Masked(key);
            }
            catch (InvalidOperationException)
            {
                // Credential Manager would not answer; Test and save says so if the key is needed.
            }
        }

        var cap = saved is null ? new ProviderConfig("", ProviderKind.Anthropic).DailyCapUsd : saved.DailyCapUsd;
        dailyCap.Value = cap is { } dollars ? (double)dollars : double.NaN;
        foreach (var box in new[] { defaultModel, fastModel })
        {
            // A model picked from the list becomes the box's text, which is all Test and save reads.
            box.SelectionChanged += (_, _) =>
            {
                if (box.SelectedItem is string id)
                {
                    box.Text = id;
                }
            };
        }

        providerUrl.TextChanged += (_, _) => ShowProvider();
        dailyCap.ValueChanged += (_, e) =>
        {
            SaveCap(e.NewValue);
            ShowSpend();
        };
        providerKeyReplace.Click += (_, _) =>
        {
            providerKeyReplacing = true;
            ShowProvider();
            providerKey.Focus(FocusState.Programmatic);
        };
        providerSave.Click += async (_, _) => await TestAndSaveProviderAsync();
        providerModels.Click += async (_, _) => await FetchModelsAsync();
        providerRemove.Click += (_, _) => RemoveProvider();
        spendMeter.Children.Add(spendFill);
        spendMeter.SizeChanged += (_, _) => ShowSpend();
        AutomationProperties.SetName(providerKey, "Key");
        AutomationProperties.SetName(providerKeyReplace, "Replace the saved key");
        AutomationProperties.SetName(dailyCap, "Daily cap in US dollars");

        var lockIcon = new FontIcon { Glyph = "\uE72E", FontSize = 13, Foreground = Palette.Brush(Palette.Ok), VerticalAlignment = VerticalAlignment.Center };
        var keySaved = new Grid { ColumnSpacing = 8 };
        keySaved.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        keySaved.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        keySaved.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(providerKeyMask, 1);
        Grid.SetColumn(providerKeyReplace, 2);
        keySaved.Children.Add(lockIcon);
        keySaved.Children.Add(providerKeyMask);
        keySaved.Children.Add(providerKeyReplace);
        providerKeySaved.Child = keySaved;
        providerKey.Width = 300;
        var keyBox = new Grid { Width = 300, Children = { providerKeySaved, providerKey } };

        providerUrlRow = Row("Server address", Note("OpenAI: https://api.openai.com/v1. OpenRouter: https://openrouter.ai/api/v1. Ollama: http://localhost:11434/v1. LM Studio: http://localhost:1234/v1."), providerUrl);
        var form = Card(
            Row("Key", Note("Kept in Windows Credential Manager and saved only after a test call works. A server on this PC usually needs none."), keyBox),
            providerUrlRow,
            Row("Default model", Note("Answers questions, drop zones and jobs. Pick one after Fetch models or type its id; left empty, the first one listed."), defaultModel),
            Row("Fast model", Note("Optional. A cheaper model for routing voice commands and jobs asked for by voice."), fastModel));

        var spendAndHistory = new Grid { ColumnSpacing = 10 };
        spendAndHistory.ColumnDefinitions.Add(new ColumnDefinition());
        spendAndHistory.ColumnDefinitions.Add(new ColumnDefinition());
        var history = HistoryCard();
        Grid.SetColumn(history, 1);
        spendAndHistory.Children.Add(SpendCard());
        spendAndHistory.Children.Add(history);

        ProviderButton(providerSave, Palette.Ink, Palette.InkHover, Palette.OnInk, 18);
        providerSave.FontWeight = FontWeights.SemiBold;
        ProviderButton(providerModels, Palette.RaisedHi, Palette.LineStrong, Palette.Ink, 16);
        ProviderButton(providerRemove, Colors.Transparent, Palette.Hover, Palette.Danger, 12);
        var buttons = new Grid { ColumnSpacing = 8 };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(providerModels, 1);
        Grid.SetColumn(providerRemove, 3);
        buttons.Children.Add(providerSave);
        buttons.Children.Add(providerModels);
        buttons.Children.Add(providerRemove);

        var cards = ProviderCards();
        ShowProvider();
        _ = LoadSpendAsync();
        ProviderStatus(saved is null ? "No provider is set up, so the AI layer is off." : $"Set up: {saved.Id}, {saved.DefaultModel}.");
        return [cards, form, spendAndHistory, buttons, providerNote];
    }

    // The three APIs side by side; the card of the one shown is raised and outlined in the accent, and the saved one says
    // Connected, since a provider is saved only after its test call worked. A click anywhere on a card picks it.
    private Grid ProviderCards()
    {
        var cards = new Grid { ColumnSpacing = 10 };
        AutomationProperties.SetName(cards, "Provider");
        for (int i = 0; i < ProviderKinds.Length; i++)
        {
            var kind = (ProviderKind)i;
            var (name, where) = ProviderKinds[i];
            var radio = new RadioButton
            {
                GroupName = "provider",
                MinWidth = 0,
                IsChecked = kind == providerShown,
                Content = new TextBlock { Text = name, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Palette.Brush(Palette.Ink) },
            };
            AutomationProperties.SetName(radio, name);
            AutomationProperties.SetHelpText(radio, where);
            var connected = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 6, Height = 6, Fill = Palette.Brush(Palette.Ok), VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = "Connected", FontSize = 11, Foreground = Palette.Brush(Palette.Ok) },
                },
            };
            var card = new Border
            {
                CornerRadius = new CornerRadius(14),
                Child = new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new Grid { Children = { radio, connected } },
                        new TextBlock { Text = where, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Brush(Palette.Ink3) },
                    },
                },
            };
            card.Tapped += (_, _) => radio.IsChecked = true;
            radio.Checked += (_, _) => PickProvider(kind);
            cards.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(card, i);
            cards.Children.Add(card);
            providerCards.Add((radio, card, connected));
        }

        return cards;
    }

    // Another API's models do not apply; picking the kind already shown keeps them.
    private void PickProvider(ProviderKind kind)
    {
        if (kind == providerShown)
        {
            return;
        }

        providerShown = kind;
        defaultModel.ItemsSource = fastModel.ItemsSource = null;
        defaultModel.Text = fastModel.Text = "";
        ShowProvider();
    }

    // The cap beside today's spend with this provider and a meter of how much of the cap that is.
    private Border SpendCard()
    {
        var spendNote = Note($"Estimated from token counts and list prices as of {PriceTable.AsOf:d MMMM yyyy}; a cloud model with no known price counts at the highest one, and a server on this PC is free. Empty means no cap, and the cap goes up to {ProviderSetup.MaxDailyCapUsd:0} dollars.");
        var capBox = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { new TextBlock { Text = "$", FontFamily = new FontFamily(Palette.Mono), FontSize = 13, Foreground = Palette.Brush(Palette.Ink3), VerticalAlignment = VerticalAlignment.Center }, dailyCap },
        };
        var capRow = Row("Daily spend cap", null, capBox);
        // Find a setting reads the explanation at the foot of the card as this row's.
        capRow.Tag = (RowParts)capRow.Tag with { Note = spendNote };
        var today = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { spentToday, new TextBlock { Text = "spent today", FontSize = 12, Foreground = Palette.Brush(Palette.Ink3), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 6) } },
        };
        return SmallCard(capRow, today, spendMeter, spendNote);
    }

    private Border HistoryCard()
    {
        var card = SmallCard(
            StackedRow("Undo after closing", Note("Undo on the island or Win+Alt+Z brings a closed answer, AI search, drop zone result or transcript back without sending it again. A request still running goes on until then."), UndoAfterClosing(), 14),
            Row("Keep history", Note($"Your last {AiHistory.MaxItems} conversations and drop zone results, on this PC only, for up to {AiHistory.MaxDays} days. Attached content is never kept. Turning it off deletes the history."), KeepHistory()),
            Row("Clear history", clearedNote, ClearHistory()));
        card.Tag = new SectionName("AI history");
        return card;
    }

    // A card of a pair side by side: its parts one under another with no lines between them.
    private static Border SmallCard(params UIElement[] parts)
    {
        var panel = new StackPanel { Spacing = 12 };
        foreach (var part in parts)
        {
            panel.Children.Add(part);
        }

        return new Border
        {
            Child = panel,
            Padding = new Thickness(18, 16, 18, 16),
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            Background = Palette.Brush(Palette.Card),
            BorderBrush = Palette.Brush(Palette.Line),
        };
    }

    private static void ProviderButton(Button button, Windows.UI.Color fill, Windows.UI.Color hover, Windows.UI.Color text, double padding)
    {
        button.Height = 38;
        button.FontSize = 13;
        button.CornerRadius = new CornerRadius(10);
        button.Padding = new Thickness(padding, 0, padding, 0);
        button.Background = Palette.Brush(fill);
        button.Foreground = Palette.Brush(text);
        button.BorderThickness = new Thickness(0);
        Look(button, "Button", "", fill, hover, text);
    }

    private void ShowProvider()
    {
        var form = FormProvider();
        var saved = store.Current.Provider;
        providerUrlRow!.Visibility = form.Kind == ProviderKind.OpenAICompatible ? Visibility.Visible : Visibility.Collapsed;
        // The saved key goes only to the host it was saved for, and shows only there.
        bool same = saved is not null && string.Equals(saved.Id, form.Id, StringComparison.OrdinalIgnoreCase);
        bool showSaved = same && providerKeyIsSaved && !providerKeyReplacing;
        providerKeySaved.Visibility = showSaved ? Visibility.Visible : Visibility.Collapsed;
        providerKey.Visibility = showSaved ? Visibility.Collapsed : Visibility.Visible;
        providerKey.PlaceholderText = same && providerKeyIsSaved ? "Leave empty to keep the saved key" : form.Kind == ProviderKind.OpenAICompatible ? "None for a server on this PC" : "Paste the key";
        providerRemove.Visibility = saved is null ? Visibility.Collapsed : Visibility.Visible;
        for (int i = 0; i < providerCards.Count; i++)
        {
            var (radio, card, connected) = providerCards[i];
            var kind = (ProviderKind)i;
            bool picked = kind == providerShown;
            radio.IsChecked = picked;
            card.Background = Palette.Brush(picked ? Palette.Raised : Palette.Card);
            card.BorderBrush = picked ? providerPicked : Palette.Brush(Palette.Outline);
            card.BorderThickness = new Thickness(picked ? 1.5 : 1);
            // The same inner size either way, so picking a card moves nothing.
            card.Padding = new Thickness(picked ? 13.5 : 14);
            connected.Visibility = saved?.Kind == kind ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private ProviderConfig FormProvider()
    {
        string? url = providerShown == ProviderKind.OpenAICompatible ? providerUrl.Text.Trim() : null;
        return new ProviderConfig(ProviderSetup.IdFor(providerShown, url), providerShown, url, ModelId(defaultModel), ModelId(fastModel), ProviderSetup.CapFrom(dailyCap.Value));
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

    // The box's text alone: the picked item is only for display (a pick also writes the text, see ProviderSection), so
    // a box the user cleared gives no model rather than the saved one back.
    private static string? ModelId(ComboBox box) => box.Text?.Trim() is { Length: > 0 } id ? id : null;

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
            providerKeyIsSaved = key is not null;
            providerKeyMask.Text = key is null ? "" : ProviderSetup.Masked(key);
            providerKeyReplacing = false;
            providerKey.Password = "";
            defaultModel.Text = tested.DefaultModel;
            ShowProvider();
            _ = LoadSpendAsync();
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
        bool hadKey = providerKeyIsSaved;
        providerKeyIsSaved = providerKeyReplacing = false;
        providerKeyMask.Text = "";
        providerKey.Password = "";
        ShowProvider();
        _ = LoadSpendAsync();
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

    // Today's spend with the saved provider, from the send log beside the settings file, read off the window's thread.
    private async Task LoadSpendAsync()
    {
        string? id = store.Current.Provider?.Id;
        string path = SendLogPath;
        try
        {
            spent = id is null ? 0 : await Task.Run(() => new SendLog(path).SpentToday(id, DateTimeOffset.Now));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // Called without waiting: an exception leaving here would end the settings process. The figure stays as it was.
        }

        spentToday.Text = $"${spent:0.00}";
        ShowSpend();
    }

    // The meter shows today's spend as a share of the cap; with no cap there is nothing to fill.
    private void ShowSpend()
    {
        double share = ProviderSetup.CapFrom(dailyCap.Value) is { } cap and > 0 ? Math.Min(1, (double)(spent / cap)) : 0;
        spendFill.Width = Math.Max(6, share * spendMeter.ActualWidth);
        spendFill.Visibility = double.IsNaN(dailyCap.Value) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Busy(bool on, string? status)
    {
        // The whole form, so nothing typed during the call is lost to what the call saves.
        foreach (var control in providerCards.Select(c => (Control)c.Radio).Concat([providerUrl, providerKey, providerKeyReplace, defaultModel, fastModel, dailyCap, providerSave, providerModels, providerRemove]))
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
        providerNote.Foreground = Palette.Brush(Palette.Ink2);
    }
}
