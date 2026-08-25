using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using RAM.Core.Roblox;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace RAM.Dialogs;

/// <summary>
/// Add accounts by pasting a .ROBLOSECURITY cookie, by signing in with a username +
/// password (the cookie is fetched through the Roblox auth API), or by pasting many
/// cookies at once. The chosen accounts are exposed via <see cref="Accounts"/>.
/// </summary>
public sealed partial class AddAccountDialog : ContentDialog
{
    private static readonly SolidColorBrush SuccessBrush = new(Color.FromArgb(0xFF, 0x6B, 0xCB, 0x77));
    private static readonly SolidColorBrush ErrorBrush = new(Color.FromArgb(0xFF, 0xFF, 0x7B, 0x72));

    private string? _signInCookie;
    private bool _signingIn;

    /// <summary>Complete error payload for the copy button (dev-facing). Null when no error.</summary>
    private string? _errorDetails;

    private bool _browserLoginActive;
    private bool _browserLoginCancelled;

    public AddAccountDialog()
    {
        this.InitializeComponent();
        MethodSelector.SelectedIndex = 0;

        // SelectionChanged is wired here rather than in XAML: the XBF connection record
        // for ComboBox.SelectionChanged makes LoadComponent throw 0x802B000A
        // ("XAML parsing failed") on this Windows App SDK version, while code-behind
        // wiring is unaffected. Same handler, same behavior.
        MethodSelector.SelectionChanged += MethodSelector_SelectionChanged;
        PrimaryButtonClick += OnPrimaryButtonClick;
    }

    /// <summary>
    /// The accounts to add (username may be null — the caller fetches it from the cookie).
    /// Empty until the dialog is confirmed.
    /// </summary>
    public IReadOnlyList<(string? Username, string Cookie)> Accounts { get; private set; } = Array.Empty<(string?, string)>();

    private void MethodSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool cookie = MethodSelector.SelectedIndex == 0;
        bool password = MethodSelector.SelectedIndex == 1;

        CookiePanel.Visibility = cookie ? Visibility.Visible : Visibility.Collapsed;
        PasswordPanel.Visibility = password ? Visibility.Visible : Visibility.Collapsed;
        BulkPanel.Visibility = !cookie && !password ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var accounts = BuildAccounts();
        if (accounts is null)
        {
            args.Cancel = true; // ErrorText already set by BuildAccounts
            return;
        }

        Accounts = accounts;
    }

    /// <summary>Build the accounts for the selected method, or null (with an inline error) when invalid.</summary>
    private List<(string? Username, string Cookie)>? BuildAccounts()
    {
        switch (MethodSelector.SelectedIndex)
        {
            case 0: // Cookie
            {
                string? cookie = RobloxCookie.Clean(CookieBox.Text);
                if (cookie is null)
                {
                    ShowError("Paste a .ROBLOSECURITY cookie to add an account.");
                    return null;
                }

                string? username = UsernameBox.Text.Trim();
                return new List<(string?, string)> { (string.IsNullOrEmpty(username) ? null : username, cookie) };
            }

            case 1: // Username + password
            {
                if (_signInCookie is null)
                {
                    ShowError("Sign in with your username and password first.");
                    return null;
                }

                string username = LoginUsernameBox.Text.Trim();
                return new List<(string?, string)> { (string.IsNullOrEmpty(username) ? null : username, _signInCookie) };
            }

            default: // Bulk cookies
            {
                var accounts = new List<(string? Username, string Cookie)>();
                foreach (var line in BulkBox.Text.Split('\n'))
                {
                    string? cookie = RobloxCookie.Clean(line);
                    if (cookie is null) continue;
                    if (accounts.Any(a => string.Equals(a.Cookie, cookie, StringComparison.Ordinal))) continue;
                    accounts.Add((null, cookie));
                }

                if (accounts.Count == 0)
                {
                    ShowError("Paste at least one .ROBLOSECURITY cookie.");
                    return null;
                }

                return accounts;
            }
        }
    }

    /// <summary>
    /// Sign in with username + password. Fast path: the Roblox auth API. When Roblox gates
    /// that with a CAPTCHA / two-step challenge (which can't be automated), fall through to
    /// the embedded-browser flow — the user finishes the challenge by hand and the cookie
    /// is captured from the browser session (port of legacy RAM's browser login).
    /// </summary>
    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (_signingIn || _browserLoginActive) return;

        string username = LoginUsernameBox.Text.Trim();
        string password = LoginPasswordBox.Password;

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            SetSignInStatus("Enter your username and password.", success: false);
            return;
        }

        _signingIn = true;
        SignInButton.IsEnabled = false;
        SetSignInStatus("Signing in…", success: true);

        try
        {
            using var client = new RobloxApiClient();
            var (cookie, error) = await client.LoginAsync(username, password);
            if (cookie is not null)
            {
                await CompleteSignInAsync(cookie, username);
                return;
            }

            string raw = client.LastLoginErrorBody ?? "(none)";
            bool gated = error?.Contains("CAPTCHA", StringComparison.OrdinalIgnoreCase) == true
                      || error?.Contains("two-step", StringComparison.OrdinalIgnoreCase) == true;
            if (gated)
            {
                SetSignInError(error!, $"Friendly: {error}\n\nRaw response:\n{raw}");
                await StartBrowserLoginAsync(username, password);
                return;
            }

            SetSignInError(error ?? "Sign-in failed.", $"Friendly: {error}\n\nRaw response:\n{raw}");
        }
        catch (Exception ex)
        {
            SetSignInError("Sign-in failed — we couldn't reach Roblox. Check your connection and try again.", ex.ToString());
        }
        finally
        {
            _signingIn = false;
            SignInButton.IsEnabled = true;
        }
    }

    /// <summary>Store the captured cookie, fetch the canonical username, and show the success state.</summary>
    private async Task CompleteSignInAsync(string cookie, string typedUsername)
    {
        string username = typedUsername;
        using var infoClient = new RobloxApiClient(cookie);
        var (canonical, _, _) = await infoClient.GetAccountInfoAsync();
        if (!string.IsNullOrEmpty(canonical))
        {
            username = canonical;
            LoginUsernameBox.Text = canonical;
        }

        _signInCookie = cookie;
        _errorDetails = null;
        CopyErrorButton.Visibility = Visibility.Collapsed;
        SetSignInStatus($"Signed in as {username} — press Add to store the account.", success: true);
    }

    // ---- Browser login (ported from legacy RAM) ----

    private const string LoginPageUrl = "https://www.roblox.com/login";

    /// <summary>
    /// Open the embedded browser on the login page, auto-fill the credentials, and wait for
    /// the user to finish any CAPTCHA / verification. Once Roblox lands on an authenticated
    /// page, capture the .ROBLOSECURITY cookie from the browser session.
    /// </summary>
    private async Task StartBrowserLoginAsync(string username, string password)
    {
        _browserLoginActive = true;
        _browserLoginCancelled = false;
        SignInButton.Visibility = Visibility.Collapsed;
        LoginUsernameBox.IsEnabled = false;
        LoginPasswordBox.IsEnabled = false;
        BrowserLoginHint.Visibility = Visibility.Visible;
        BrowserLoginHost.Visibility = Visibility.Visible;
        CancelBrowserButton.Visibility = Visibility.Visible;
        SetSignInStatus("Signing in with the browser — complete any CAPTCHA shown to finish.", success: true);

        try
        {
            await LoginWebView.EnsureCoreWebView2Async();
            var web = LoginWebView.CoreWebView2;
            if (web is null)
            {
                SetSignInError("The built-in browser isn't available — sign in on roblox.com first, then add the account by cookie.", "WebView2 runtime not available.");
                return;
            }

            web.Navigate(LoginPageUrl);

            // Auto-fill the login form once it renders (React form — retry until present).
            string fillScript = BuildFillScript(username, password);
            for (int attempt = 0; attempt < 60 && !_browserLoginCancelled; attempt++)
            {
                try
                {
                    if (await web.ExecuteScriptAsync(fillScript) == "true") break;
                }
                catch
                {
                    // Page mid-navigation — retry on the next tick.
                }
                await Task.Delay(400);
            }

            // Wait for the user to complete login; the page redirects to an authenticated URL.
            var deadline = DateTime.UtcNow.AddMinutes(5);
            while (!_browserLoginCancelled && DateTime.UtcNow < deadline)
            {
                await Task.Delay(500);
                string url = LoginWebView.Source?.ToString().ToLowerInvariant() ?? "";
                if (!IsAuthenticatedUrl(url)) continue;

                var cookies = await web.CookieManager.GetCookiesAsync("https://www.roblox.com");
                var securityCookie = cookies.FirstOrDefault(c => c.Name == ".ROBLOSECURITY");
                if (securityCookie is null)
                {
                    SetSignInError("Signed in, but Roblox didn't issue a session cookie — try again, or add the account by cookie.", $"URL: {url}");
                    return;
                }

                await CompleteSignInAsync(securityCookie.Value, LoginUsernameBox.Text);
                return;
            }

            SetSignInError("Browser sign-in timed out or was cancelled — try again, or add the account by cookie.", "Timed out after 5 minutes waiting for login.");
        }
        catch (Exception ex)
        {
            SetSignInError("Browser sign-in failed — sign in on roblox.com first, then add the account by cookie.", ex.ToString());
        }
        finally
        {
            _browserLoginActive = false;
            EndBrowserLoginUI();
        }
    }

    private void CancelBrowser_Click(object sender, RoutedEventArgs e)
    {
        _browserLoginCancelled = true;
        SetSignInStatus("Browser sign-in cancelled.", success: false);
    }

    /// <summary>Restore the username/password form after the browser flow ends.</summary>
    private void EndBrowserLoginUI()
    {
        BrowserLoginHost.Visibility = Visibility.Collapsed;
        BrowserLoginHint.Visibility = Visibility.Collapsed;
        CancelBrowserButton.Visibility = Visibility.Collapsed;
        LoginUsernameBox.IsEnabled = true;
        LoginPasswordBox.IsEnabled = true;
        SignInButton.Visibility = Visibility.Visible;
    }

    /// <summary>Roblox pages that only exist when signed in (the same whitelist legacy RAM uses).</summary>
    private static bool IsAuthenticatedUrl(string url)
    {
        if (url.Contains("/login") || url.Contains("/createaccount")) return false;
        return new[] { "/home", "/games", "/catalog", "/avatar", "/discover", "/friends", "/profile", "/groups", "/develop", "/create", "/transactions", "/my/avatar", "/users/" }
            .Any(p => url.Contains(p));
    }

    /// <summary>Fill the React login form with the native value setter so Roblox's JS sees the input.</summary>
    private static string BuildFillScript(string username, string password)
    {
        string u = JsonSerializer.Serialize(username);
        string p = JsonSerializer.Serialize(password);
        return
            "(function() {" +
            "function setNativeValue(el, value) {" +
            "  var proto = Object.getPrototypeOf(el);" +
            "  var setter = Object.getOwnPropertyDescriptor(proto, 'value').set;" +
            "  setter.call(el, value);" +
            "  el.dispatchEvent(new Event('input', { bubbles: true }));" +
            "}" +
            "var userEl = document.getElementById('login-username');" +
            "var passEl = document.getElementById('login-password');" +
            "var btn = document.getElementById('login-button');" +
            "if (userEl && passEl && btn) {" +
            "  setNativeValue(userEl, " + u + ");" +
            "  setNativeValue(passEl, " + p + ");" +
            "  setTimeout(function() { btn.click(); }, 300);" +
            "  return true;" +
            "}" +
            "return false;" +
            "})();";
    }

    /// <summary>Show a friendly sign-in error and reveal the copy-details button for support/dev.</summary>
    private void SetSignInError(string friendly, string details)
    {
        _errorDetails = details;
        CopyErrorButton.Visibility = Visibility.Visible;
        CopyErrorButton.Content = "Copy error details";
        SetSignInStatus(friendly, success: false);
    }

    private void CopyError_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_errorDetails)) return;
        var package = new DataPackage();
        package.SetText(_errorDetails);
        Clipboard.SetContent(package);
        CopyErrorButton.Content = "Copied ✓";
    }

    private void SetSignInStatus(string message, bool success)
    {
        SignInStatus.Text = message;
        SignInStatus.Foreground = success ? SuccessBrush : ErrorBrush;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
