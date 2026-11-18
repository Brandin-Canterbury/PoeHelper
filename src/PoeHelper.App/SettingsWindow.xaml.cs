using System.Windows;
using System.Windows.Controls;
using PoeHelper.App.Native;
using PoeHelper.Core;

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
        foreach (var l in leagues) League.Items.Add(l);
        League.Text = settings.League;
        PriceHotkey.Text = settings.PriceCheckHotkey;
        CompareHotkey.Text = settings.CompareHotkey;
        Advanced.IsChecked = settings.UseAdvancedCopy;
        Contact.Text = settings.Contact;
        Status.SelectedItem = Status.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == settings.PriceCheckStatus) ?? Status.Items[0];
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
        _settings.AccountName = account;
        _settings.CharacterName = Character.Text.Trim();
        _settings.League = string.IsNullOrWhiteSpace(League.Text) ? "Covens" : League.Text.Trim();
        _settings.PriceCheckHotkey = PriceHotkey.Text.Trim();
        _settings.CompareHotkey = CompareHotkey.Text.Trim();
        _settings.UseAdvancedCopy = Advanced.IsChecked == true;
        _settings.Contact = Contact.Text.Trim();
        _settings.PriceCheckStatus = (string)((ComboBoxItem)Status.SelectedItem).Tag;
        _settings.Save();
        DialogResult = true;
    }
}
