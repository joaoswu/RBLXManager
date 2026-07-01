using System;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;

namespace RblxManager
{
    public partial class LoginWindow : Window
    {
        // Event triggered when a valid Roblox security cookie is discovered in the session
        public event EventHandler<string>? CookieFound;

        public LoginWindow()
        {
            InitializeComponent();
            InitializeWebView();
        }

        private async void InitializeWebView()
        {
            try
            {
                // Initialize Edge Chromium WebView2 engine asynchronously
                await WebView.EnsureCoreWebView2Async(null);
                WebView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"WebView2 Runtime Initialization Failed:\n{ex.Message}\n\nPlease install the official Microsoft Edge WebView2 Runtime.",
                    "Dependency Error", MessageBoxButton.OK, MessageBoxImage.Error);
                this.Close();
            }
        }

        private async void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (WebView.CoreWebView2 == null) return;

            try
            {
                // Read cookies from the active session
                var cookieManager = WebView.CoreWebView2.CookieManager;
                var cookies = await cookieManager.GetCookiesAsync("https://www.roblox.com");

                foreach (var cookie in cookies)
                {
                    if (cookie.Name == ".ROBLOSECURITY")
                    {
                        string cookieValue = cookie.Value;
                        if (!string.IsNullOrWhiteSpace(cookieValue))
                        {
                            // Trigger callback event
                            CookieFound?.Invoke(this, cookieValue);

                            // Clear cookie from the local browser instance immediately.
                            // This ensures the next login starts fresh instead of auto-logging into the same account.
                            cookieManager.DeleteCookie(cookie);

                            this.Close();
                            break;
                        }
                    }
                }
            }
            catch
            {
                // Silent catch for transient check errors
            }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                this.DragMove();
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
