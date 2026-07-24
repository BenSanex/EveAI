using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Eva.App;

public sealed class App : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());

        Styles.Add(new Style(selector => selector.OfType<Button>())
        {
            Setters =
            {
                new Setter(Button.BackgroundProperty, new SolidColorBrush(Color.FromArgb(115, 3, 42, 62))),
                new Setter(Button.ForegroundProperty, new SolidColorBrush(Color.Parse("#9DEBFF"))),
                new Setter(Button.BorderBrushProperty, new SolidColorBrush(Color.Parse("#27C7F2"))),
                new Setter(Button.BorderThicknessProperty, new Thickness(1)),
                new Setter(Button.CornerRadiusProperty, new CornerRadius(0)),
                new Setter(Button.PaddingProperty, new Thickness(15, 9)),
                new Setter(Button.FontWeightProperty, FontWeight.SemiBold)
            }
        });
        Styles.Add(new Style(selector => selector.OfType<TextBox>())
        {
            Setters =
            {
                new Setter(TextBox.BackgroundProperty, new SolidColorBrush(Color.FromArgb(105, 1, 17, 31))),
                new Setter(TextBox.ForegroundProperty, new SolidColorBrush(Color.Parse("#D8F7FF"))),
                new Setter(TextBox.CaretBrushProperty, new SolidColorBrush(Color.Parse("#42D9FF"))),
                new Setter(TextBox.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(170, 30, 155, 193))),
                new Setter(TextBox.BorderThicknessProperty, new Thickness(1)),
                new Setter(TextBox.CornerRadiusProperty, new CornerRadius(0)),
                new Setter(TextBox.SelectionBrushProperty, new SolidColorBrush(Color.FromArgb(120, 24, 164, 208)))
            }
        });
        Styles.Add(new Style(selector => selector.OfType<ComboBox>())
        {
            Setters =
            {
                new Setter(ComboBox.BackgroundProperty, new SolidColorBrush(Color.FromArgb(150, 1, 25, 42))),
                new Setter(ComboBox.ForegroundProperty, new SolidColorBrush(Color.Parse("#BCEFFF"))),
                new Setter(ComboBox.BorderBrushProperty, new SolidColorBrush(Color.Parse("#228BB0"))),
                new Setter(ComboBox.BorderThicknessProperty, new Thickness(1)),
                new Setter(ComboBox.CornerRadiusProperty, new CornerRadius(0))
            }
        });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
