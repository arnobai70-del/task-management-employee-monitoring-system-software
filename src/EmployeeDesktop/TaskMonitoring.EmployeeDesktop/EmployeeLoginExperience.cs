using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace TaskMonitoring.EmployeeDesktop;

internal static class EmployeeLoginExperience
{
    private static readonly Brush TextBrush = Brush("#0F172A");
    private static readonly Brush MutedBrush = Brush("#64748B");
    private static readonly Brush LineBrush = Brush("#E2E8F0");
    private static readonly Brush AccentBrush = Brush("#2563EB");
    private static readonly Brush SoftBlueBrush = Brush("#EFF6FF");

    public static void Apply(Window window)
    {
        if (window.FindName("LoginShell") is not ScrollViewer loginShell ||
            window.FindName("ServerUrlBox") is not TextBox serverUrlBox ||
            window.FindName("EmailBox") is not TextBox emailBox ||
            window.FindName("PasswordBox") is not PasswordBox passwordBox ||
            window.FindName("LoginButton") is not Button loginButton)
        {
            return;
        }

        if (Equals(loginShell.Tag, "clean-login-v3"))
        {
            return;
        }

        loginShell.Tag = "clean-login-v3";
        loginShell.Content = null;
        loginShell.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        loginShell.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        loginShell.Background = Brush("#F6F8FC");

        NormalizeInput(serverUrlBox);
        NormalizeInput(emailBox);
        NormalizeInput(passwordBox);

        loginButton.Margin = new Thickness(0, 22, 0, 0);
        loginButton.Height = 46;
        loginButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        loginButton.HorizontalContentAlignment = HorizontalAlignment.Center;
        loginButton.Content = "Sign in";

        var root = new Grid
        {
            Margin = new Thickness(28),
            MinHeight = 540
        };

        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var brand = BuildBrand();
        Grid.SetRow(brand, 0);
        root.Children.Add(brand);

        var card = BuildCard(serverUrlBox, emailBox, passwordBox, loginButton);
        Grid.SetRow(card, 1);
        root.Children.Add(card);

        var footer = new TextBlock
        {
            Text = "TaskMonitoring Employee Workspace",
            Foreground = MutedBrush,
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 18, 0, 2)
        };
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        loginShell.Content = root;
        emailBox.Focus();
    }

    private static FrameworkElement BuildBrand()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 18)
        };

        var logo = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(10),
            Background = AccentBrush,
            Child = new TextBlock
            {
                Text = "TM",
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var copy = new StackPanel
        {
            Margin = new Thickness(11, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        copy.Children.Add(new TextBlock
        {
            Text = "TaskMonitoring",
            Foreground = TextBrush,
            FontWeight = FontWeights.SemiBold,
            FontSize = 15
        });
        copy.Children.Add(new TextBlock
        {
            Text = "Employee Workspace",
            Foreground = MutedBrush,
            FontSize = 10.5,
            Margin = new Thickness(0, 2, 0, 0)
        });

        row.Children.Add(logo);
        row.Children.Add(copy);
        return row;
    }

    private static FrameworkElement BuildCard(
        TextBox serverUrlBox,
        TextBox emailBox,
        PasswordBox passwordBox,
        Button loginButton)
    {
        var card = new Border
        {
            Width = 470,
            MaxWidth = 470,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.White,
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(34, 30, 34, 28),
            Effect = new DropShadowEffect
            {
                Color = Color.FromRgb(15, 23, 42),
                Opacity = 0.10,
                BlurRadius = 28,
                ShadowDepth = 8,
                Direction = 270
            }
        };

        var content = new StackPanel();

        var eyebrow = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = SoftBlueBrush,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(9, 5, 9, 5),
            Margin = new Thickness(0, 0, 0, 16)
        };
        eyebrow.Child = new TextBlock
        {
            Text = "EMPLOYEE ACCESS",
            Foreground = AccentBrush,
            FontSize = 9.5,
            FontWeight = FontWeights.Bold
        };
        content.Children.Add(eyebrow);

        content.Children.Add(new TextBlock
        {
            Text = "Sign in to your workspace",
            Foreground = TextBrush,
            FontSize = 27,
            FontWeight = FontWeights.SemiBold
        });
        content.Children.Add(new TextBlock
        {
            Text = "Use your organization account to access assigned work, attendance and company resources.",
            Foreground = MutedBrush,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 19,
            Margin = new Thickness(0, 7, 0, 24)
        });

        content.Children.Add(Label("EMAIL"));
        emailBox.Margin = new Thickness(0, 7, 0, 16);
        content.Children.Add(emailBox);

        content.Children.Add(Label("PASSWORD"));
        passwordBox.Margin = new Thickness(0, 7, 0, 0);
        content.Children.Add(passwordBox);

        content.Children.Add(loginButton);

        var connection = new Expander
        {
            Header = "Connection settings",
            IsExpanded = false,
            Foreground = MutedBrush,
            FontSize = 11.5,
            Margin = new Thickness(0, 20, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        var connectionContent = new StackPanel
        {
            Margin = new Thickness(0, 10, 0, 0)
        };
        connectionContent.Children.Add(Label("SERVER"));
        serverUrlBox.Margin = new Thickness(0, 7, 0, 0);
        connectionContent.Children.Add(serverUrlBox);
        connection.Content = connectionContent;
        content.Children.Add(connection);

        var security = new Border
        {
            Background = Brush("#F8FAFC"),
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 18, 0, 0)
        };
        var securityRow = new Grid();
        securityRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        securityRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var dot = new Border
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = Brush("#10B981"),
            Margin = new Thickness(1, 5, 9, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        securityRow.Children.Add(dot);
        var securityText = new TextBlock
        {
            Text = "Session tokens stay in memory and are not written to plaintext files or logs.",
            Foreground = MutedBrush,
            FontSize = 10.8,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 16
        };
        Grid.SetColumn(securityText, 1);
        securityRow.Children.Add(securityText);
        security.Child = securityRow;
        content.Children.Add(security);

        card.Child = content;
        return card;
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        Foreground = MutedBrush,
        FontSize = 10,
        FontWeight = FontWeights.Bold
    };

    private static void NormalizeInput(Control control)
    {
        control.Height = 44;
        control.Margin = new Thickness(0);
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        control.VerticalAlignment = VerticalAlignment.Center;
    }

    private static SolidColorBrush Brush(string hex)
        => new((Color)ColorConverter.ConvertFromString(hex));
}
