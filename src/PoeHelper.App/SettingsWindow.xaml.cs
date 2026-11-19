using System.Windows;
using System.Windows.Controls;
using PoeHelper.App.Native;
using PoeHelper.Core;
using PoeHelper.Core.Build;
using PoeHelper.Core.Trade;

namespace PoeHelper.App;

public partial class SettingsWindow : Window
{
    /// <summary>One tickable stat in the upgrade tracker.</summary>
    public sealed class TrackedChoice(string key, string label, bool ticked)
    {
        public string Key => key;
        public string Label => label;
        public bool Ticked { get; set; } = ticked;
    }

    const string AutoSkill = "Auto (most linked supports)";
    string? _detectedSkill;
    bool _hasSkills;

    /// <summary>Says which skill is scored and whether it switches on a skill-specific formula.</summary>
    void UpdateSkillHint()
    {
        var text = SkillPick.Text.Trim();
        var scored = text == AutoSkill || text.Length == 0 ? _detectedSkill : text;
        var hint = text == AutoSkill || text.Length == 0
            ? (_hasSkills ? $"Auto picks {_detectedSkill ?? "nothing"} on this character." : "Your gems appear here once your character has loaded. You can also type a gem name.")
            : "";
        if (SkillFormulas.ForName(scored) is { } f) hint = (hint + " " + f.Summary).Trim();
        SkillHint.Text = hint;
    }
    readonly AppSettings _settings;
    readonly List<TrackedChoice> _tracked;

    public SettingsWindow(AppSettings settings, IEnumerable<string> leagues, IReadOnlyList<string>? skills = null, string? detectedSkill = null)
    {
        InitializeComponent();
        _settings = settings;
        Account.Text = settings.AccountName;
        Character.Text = settings.CharacterName;
        SkillPick.Items.Add(AutoSkill);
        foreach (var s in skills ?? []) SkillPick.Items.Add(s);
        SkillPick.Text = string.IsNullOrWhiteSpace(settings.MainSkill) ? AutoSkill : settings.MainSkill;
        _detectedSkill = detectedSkill;
        _hasSkills = skills is { Count: > 0 };
        UpdateSkillHint();
        SkillPick.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => UpdateSkillHint()));
        SkillPick.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(UpdateSkillHint);
        var on = TrackedStats.Resolve(settings.TrackedStats).Select(t => t.Key).ToHashSet();
        _tracked = TrackedStats.All.Select(t => new TrackedChoice(t.Key, t.Label, on.Contains(t.Key))).ToList();
        Tracked.ItemsSource = _tracked;
        foreach (var l in leagues) League.Items.Add(l);
        League.Text = settings.League;
        PriceHotkey.Text = settings.PriceCheckHotkey;
        CompareHotkey.Text = settings.CompareHotkey;
        Advanced.IsChecked = settings.UseAdvancedCopy;
        Contact.Text = settings.Contact;
        Status.SelectedItem = Status.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == settings.PriceCheckStatus) ?? Status.Items[0];
        var session = SessionStore.Get(settings);
        SessionId.Password = session ?? "";
        SessionStatus.Text = session is null ? "Not logged in: searches are anonymous." : "Session saved. Press Test to check it still works.";
    }

    void OnSessionHelp(object sender, RoutedEventArgs e) => new SessionHelpWindow { Owner = this }.Show();

    void OnSessionClear(object sender, RoutedEventArgs e)
    {
        SessionId.Password = "";
        SessionStatus.Text = "Cleared. Press Save to forget the stored session.";
    }

    async void OnSessionTest(object sender, RoutedEventArgs e)
    {
        var value = SessionId.Password.Trim();
        if (!SessionStore.LooksValid(value))
        {
            SessionStatus.Text = "That doesn't look like a POESESSID (it's 32 letters and digits, 0-9 and a-f). See \"How do I get this?\".";
            return;
        }
        SessionStatus.Text = "Checking with pathofexile.com…";
        try
        {
            using var client = new TradeClient(TradeClient.BuildUserAgent(Contact.Text), value);
            bool ok = await client.CheckLoginAsync();
            SessionStatus.Text = ok
                ? "✓ Logged in. Press Save to keep it."
                : "✗ pathofexile.com didn't accept it. It may have expired (logging out of the website ends it). Copy a fresh one.";
        }
        catch (Exception ex)
        {
            SessionStatus.Text = "Couldn't reach pathofexile.com: " + ex.Message;
        }
    }

    void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            Hotkey.Parse(PriceHotkey.Text);
            Hotkey.Parse(CompareHotkey.Text);
        }
        catch (FormatException ex)
        {
            ErrorText.Text = ex.Message;
            return;
        }
        var account = Account.Text.Trim();
        if (account.Length > 0 && !account.Contains('#'))
        {
            ErrorText.Text = "Account name needs its #1234 discriminator (shown on your pathofexile.com profile).";
            return;
        }
        var session = SessionId.Password.Trim();
        if (session.Length > 0 && !SessionStore.LooksValid(session))
        {
            ErrorText.Text = "The session cookie should be 32 letters and digits (0-9, a-f). Clear it or paste the POESESSID value again.";
            return;
        }
        var tracked = _tracked.Where(t => t.Ticked).Select(t => t.Key).ToList();
        if (tracked.Count == 0)
        {
            ErrorText.Text = "Tick at least one stat in the upgrade tracker.";
            return;
        }
        _settings.AccountName = account;
        _settings.CharacterName = Character.Text.Trim();
        var skill = SkillPick.Text.Trim();
        _settings.MainSkill = skill == AutoSkill ? "" : skill;
        _settings.TrackedStats = tracked;
        _settings.League = string.IsNullOrWhiteSpace(League.Text) ? "Covens" : League.Text.Trim();
        _settings.PriceCheckHotkey = PriceHotkey.Text.Trim();
        _settings.CompareHotkey = CompareHotkey.Text.Trim();
        _settings.UseAdvancedCopy = Advanced.IsChecked == true;
        _settings.Contact = Contact.Text.Trim();
        _settings.PriceCheckStatus = (string)((ComboBoxItem)Status.SelectedItem).Tag;
        SessionStore.Set(_settings, session);
        _settings.Save();
        DialogResult = true;
    }
}
