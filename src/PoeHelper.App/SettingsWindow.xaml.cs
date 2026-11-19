using System.Windows;
using System.Windows.Controls;
using PoeHelper.App.Native;
using PoeHelper.Core;
using PoeHelper.Core.Trade;

namespace PoeHelper.App;

public partial class SettingsWindow : Window
{
    readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings, IEnumerable<string> leagues)
    {
        InitializeComponent();
        _settings = settings;
        Account.Text = settings.AccountName;
        Character.Text = settings.CharacterName;
        MainSkill.Text = settings.MainSkill;
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
        _settings.AccountName = account;
        _settings.CharacterName = Character.Text.Trim();
        _settings.MainSkill = MainSkill.Text.Trim();
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
