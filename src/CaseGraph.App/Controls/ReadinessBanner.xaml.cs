using CaseGraph.App.Services;
using System.Windows;
using System.Windows.Controls;

namespace CaseGraph.App.Controls;

public partial class ReadinessBanner : UserControl
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State),
        typeof(ReadinessBannerState),
        typeof(ReadinessBanner),
        new PropertyMetadata(ReadinessBannerState.Hidden)
    );

    public ReadinessBanner()
    {
        InitializeComponent();
    }

    public ReadinessBannerState State
    {
        get => (ReadinessBannerState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }
}
