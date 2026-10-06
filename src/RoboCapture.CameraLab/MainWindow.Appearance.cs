using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace RoboCapture.CameraLab;

public sealed partial class MainWindow
{
    private static SolidColorBrush Ink(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    private void ConfigureAppearance()
    {
        Background = Ink("#EEF2F5");
        Foreground = Ink("#182B3A");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Resources = (ResourceDictionary)XamlReader.Parse("""
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Style TargetType="Button">
                <Setter Property="Background" Value="#E8EEF2"/>
                <Setter Property="Foreground" Value="#203B4B"/>
                <Setter Property="BorderBrush" Value="#CAD6DF"/>
                <Setter Property="BorderThickness" Value="1"/>
                <Setter Property="Padding" Value="16,10"/>
                <Setter Property="Margin" Value="0,4,8,4"/>
                <Setter Property="MinHeight" Value="38"/>
                <Setter Property="FontWeight" Value="SemiBold"/>
                <Setter Property="Cursor" Value="Hand"/>
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="Button">
                      <Border x:Name="Surface" Background="{TemplateBinding Background}"
                              BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"
                              CornerRadius="8" Padding="{TemplateBinding Padding}">
                        <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                      </Border>
                      <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Surface" Property="Opacity" Value="0.82"/></Trigger>
                        <Trigger Property="IsPressed" Value="True"><Setter TargetName="Surface" Property="Opacity" Value="0.65"/></Trigger>
                        <Trigger Property="IsKeyboardFocused" Value="True">
                          <Setter TargetName="Surface" Property="BorderBrush" Value="#087F8C"/>
                          <Setter TargetName="Surface" Property="BorderThickness" Value="3"/>
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Surface" Property="Opacity" Value="0.45"/></Trigger>
                      </ControlTemplate.Triggers>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
              </Style>
              <Style TargetType="TextBox">
                <Setter Property="Padding" Value="10,7"/>
                <Setter Property="Margin" Value="0,3,8,3"/>
                <Setter Property="MinHeight" Value="36"/>
                <Setter Property="Background" Value="White"/>
                <Setter Property="Foreground" Value="#182B3A"/>
                <Setter Property="BorderBrush" Value="#C5D2DC"/>
                <Setter Property="VerticalContentAlignment" Value="Center"/>
              </Style>
              <Style TargetType="ComboBox">
                <Setter Property="MinHeight" Value="36"/>
                <Setter Property="Padding" Value="10,6"/>
                <Setter Property="Margin" Value="0,3,8,3"/>
                <Setter Property="VerticalContentAlignment" Value="Center"/>
              </Style>
              <Style TargetType="Label">
                <Setter Property="VerticalContentAlignment" Value="Center"/>
                <Setter Property="Padding" Value="0,8,8,8"/>
                <Setter Property="Foreground" Value="#526A7A"/>
              </Style>
              <Style TargetType="TextBlock">
                <Setter Property="TextWrapping" Value="Wrap"/>
              </Style>
              <Style TargetType="Expander">
                <Setter Property="Margin" Value="0,8,0,0"/>
                <Setter Property="Padding" Value="4"/>
                <Setter Property="Foreground" Value="#526A7A"/>
              </Style>
            </ResourceDictionary>
            """);
    }

    private static void Emphasize(Button button, bool capture = false)
    {
        button.Background = Ink(capture ? "#F5B544" : "#087F8C");
        button.Foreground = capture ? Ink("#192B36") : Brushes.White;
        button.BorderBrush = button.Background;
        button.FontSize = capture ? 20 : 16;
        button.MinHeight = capture ? 58 : 48;
        button.MinWidth = capture ? 210 : 180;
        button.Padding = new Thickness(24, 12, 24, 12);
    }

    private static Border Card(string number, string title, string description, IEnumerable<UIElement> controls)
    {
        var content = new StackPanel();
        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        if (number.Length > 0)
            heading.Children.Add(new Border {
                Background = Ink("#E0F1F0"), CornerRadius = new CornerRadius(9),
                Width = 38, Height = 38, Margin = new Thickness(0, 0, 12, 0),
                Child = new TextBlock { Text = number, Foreground = Ink("#087F8C"), FontWeight = FontWeights.Bold,
                    FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            });
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold });
        titles.Children.Add(new TextBlock { Text = description, FontSize = 12, Foreground = Ink("#617789"), Margin = new Thickness(0, 3, 0, 0) });
        heading.Children.Add(titles);
        content.Children.Add(heading);
        foreach (var element in controls) content.Children.Add(element);
        return new Border { Background = Brushes.White, BorderBrush = Ink("#DCE5EB"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14), Padding = new Thickness(20), Margin = new Thickness(0, 0, 0, 14), Child = content };
    }

    private UIElement ComposeStudioLayout(StackPanel original)
    {
        // Retain the existing controls and event handlers while regrouping the workflow.
        var children = original.Children.Cast<UIElement>().ToArray();
        original.Children.Clear();
        var groups = new Dictionary<string, List<UIElement>>();
        string section = "";
        foreach (var child in children.Skip(1))
        {
            if (child is TextBlock title && Equals(title.Tag, "studio-section"))
            {
                section = title.Text;
                groups[section] = new List<UIElement>();
            }
            else groups[section].Add(child);
        }
        var shell = new DockPanel();
        var branding = new StackPanel();
        branding.Children.Add(new TextBlock { Text = "ROBOCAPTURE  /  STUDIO", Foreground = Ink("#66D5CE"), FontSize = 12, FontWeight = FontWeights.Bold });
        branding.Children.Add(new TextBlock { Text = "Make every frame count.", FontSize = 28, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 5, 0, 10) });
        ((Panel)children[0]).Children.Clear();
        _cameraText.Foreground = Ink("#BFCDD9");
        _cameraText.FontSize = 12;
        _statusText.Foreground = Ink("#80E0D3");
        _statusText.FontWeight = FontWeights.SemiBold;
        branding.Children.Add(_cameraText);
        branding.Children.Add(_statusText);
        var banner = new Border { Background = Ink("#122B3B"), Padding = new Thickness(28, 20, 28, 18), Child = branding };
        DockPanel.SetDock(banner, Dock.Top);
        shell.Children.Add(banner);

        var body = new StackPanel { Margin = new Thickness(24, 20, 24, 24), MaxWidth = 1400 };
        var setup = groups["1. CHOOSE CAMERA"];
        var advanced = new StackPanel();
        advanced.Children.Add(setup[2]);
        advanced.Children.Add(setup[3]);
        setup.RemoveRange(2, 2);
        setup.Add(new Expander { Header = "Advanced camera settings", Content = advanced });
        var connection = groups["2. CONNECT"];
        var connectionControls = (Panel)connection[0];
        ((Button)connectionControls.Children[0]).Content = "Connect camera";
        Emphasize((Button)connectionControls.Children[0]);
        setup.Add(connection[0]);
        _detectStatus.FontSize = 12;
        _detectStatus.Foreground = Ink("#617789");

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition());
        var cameraCard = Card("01", "Choose & connect", "Select your camera, then establish a connection.", setup);
        cameraCard.Margin = new Thickness(0, 0, 14, 14);
        top.Children.Add(cameraCard);
        var saveCard = Card("02", "Set your destination", "Choose where your photos go and their file format.", groups["SAVE SETTINGS"]);
        Grid.SetColumn(saveCard, 1);
        top.Children.Add(saveCard);
        body.Children.Add(top);
        // Stack setup cards on narrower displays.
        top.SizeChanged += (_, _) => {
            var narrow = top.ActualWidth < 960;
            top.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            if (top.RowDefinitions.Count == 0) { top.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); top.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); }
            Grid.SetColumn(saveCard, narrow ? 0 : 1);
            Grid.SetRow(saveCard, narrow ? 1 : 0);
            cameraCard.Margin = new Thickness(0, 0, narrow ? 0 : 14, 14);
        };

        var capture = groups["3. CAPTURE"];
        var quality = capture[0];
        capture.RemoveAt(0);
        capture.Add(new Expander { Header = "Experimental quality scoring", Content = quality });
        _qualityEnabled.Content = "Check captured JPEGs for blink / smile quality (experimental)";
        _saveFolder.MinWidth = 200;
        _saveFolder.Width = 250;
        _moduleFolder.MinWidth = 200;
        _moduleFolder.Width = 280;
        _log.FontFamily = new FontFamily("Consolas");
        _log.FontSize = 12;
        _log.Background = Ink("#122B3B");
        _log.Foreground = Ink("#C4D9E6");
        body.Children.Add(Card("03", "Capture the moment", "Set your subject. Preview your frame. Take the shot.", capture));

        var extras = new StackPanel();
        foreach (var item in groups["ROSTER (optional)"]) extras.Children.Add(item);
        body.Children.Add(new Expander { Header = "Roster & subject matching", Content = extras });
        var diagnostics = new StackPanel();
        foreach (var item in groups["SIMULATOR TESTING (Simulator driver only)"]) diagnostics.Children.Add(item);
        body.Children.Add(new Expander { Header = "Simulator tools", Content = diagnostics });
        body.Children.Add(new Expander { Header = "Activity log", Content = _log });
        shell.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        return shell;
    }
}
