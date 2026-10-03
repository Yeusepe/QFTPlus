using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace QFTPlus;

public partial class NumericSetting : UserControl
{
    public NumericSetting() => InitializeComponent();
    internal static Slider AddTo(Panel parent, string title, double min, double max, double value, Action<double> changed, string unit = "%", double scale = 1, int decimals = 2)
    {
        var view = new NumericSetting();
        var (slider, number) = (view.Track, view.Number);
        (slider.Minimum, slider.Maximum, slider.Value, slider.SmallChange, slider.LargeChange) = (min, max, Math.Clamp(value, min, max), (max - min) / 100, (max - min) / 10);
        (number.Minimum, number.Maximum, number.SmallChange, number.LargeChange, number.MaxDecimalPlaces) = (min * scale, max * scale, (max - min) * scale / 100, (max - min) * scale / 10, decimals);
        (view.Title.Text, view.Unit.Text) = (title, unit);
        number.ToolTip = $"Enter a value from {Math.Round(min * scale, decimals)} to {Math.Round(max * scale, decimals)}{unit}.";
        AutomationProperties.SetName(number, title + " value");
        AutomationProperties.SetHelpText(number, (string)number.ToolTip);
        number.SetBinding(Wpf.Ui.Controls.NumberBox.ValueProperty, new Binding(nameof(Slider.Value)) { Source = slider, Converter = new Scale(scale), UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        number.LostKeyboardFocus += (_, _) =>
        {
            if (PresentationSource.FromVisual(number) is { } source) number.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyUpEvent });
        };
        number.ValueChanged += (_, _) => { if (number.Value is { } typed && !double.IsFinite(typed)) number.Value = slider.Value * scale; };
        slider.ValueChanged += (_, _) => changed(slider.Value);
        parent.Children.Add(view);
        return slider;
    }

    sealed class Scale(double scale) : IValueConverter
    {
        public object Convert(object value, Type type, object parameter, CultureInfo culture) => (double)value * scale;
        public object ConvertBack(object value, Type type, object parameter, CultureInfo culture) => value is double number && double.IsFinite(number) ? number / scale : DependencyProperty.UnsetValue;
    }
}
