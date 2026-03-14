using CaseGraph.App;
using System.Runtime.ExceptionServices;
using System.Reflection;
using System.Threading;
using System.Windows;

namespace CaseGraph.Infrastructure.Tests;

public sealed class MainWindowShellTests
{
    [Theory]
    [InlineData(WindowState.Normal, WindowState.Maximized)]
    [InlineData(WindowState.Maximized, WindowState.Normal)]
    [InlineData(WindowState.Minimized, WindowState.Maximized)]
    public void GetToggledWindowState_ReturnsExpectedState(
        WindowState currentState,
        WindowState expectedState
    )
    {
        var toggledState = InvokeStatic<WindowState>(
            "GetToggledWindowState",
            currentState
        );

        Assert.Equal(expectedState, toggledState);
    }

    [Theory]
    [InlineData(ResizeMode.CanResize, true)]
    [InlineData(ResizeMode.CanResizeWithGrip, true)]
    [InlineData(ResizeMode.CanMinimize, false)]
    [InlineData(ResizeMode.NoResize, false)]
    public void CanToggleMaximizeRestore_MatchesResizeMode(
        ResizeMode resizeMode,
        bool expected
    )
    {
        var canToggle = InvokeStatic<bool>("CanToggleMaximizeRestore", resizeMode);

        Assert.Equal(expected, canToggle);
    }

    [Fact]
    public void CalculateRestoredLeft_PreservesCursorRatio()
    {
        var restoredLeft = InvokeStatic<double>(
            "CalculateRestoredLeft",
            1200d,
            900d,
            0.25d
        );

        Assert.Equal(975d, restoredLeft, precision: 6);
    }

    [Fact]
    public void HasExceededDragThreshold_UsesSystemThresholds()
    {
        var belowThreshold = InvokeStatic<bool>(
            "HasExceededDragThreshold",
            new Point(100d, 100d),
            new Point(
                100d + SystemParameters.MinimumHorizontalDragDistance - 1d,
                100d
            )
        );
        var aboveThreshold = InvokeStatic<bool>(
            "HasExceededDragThreshold",
            new Point(100d, 100d),
            new Point(
                100d + SystemParameters.MinimumHorizontalDragDistance,
                100d
            )
        );

        Assert.False(belowThreshold);
        Assert.True(aboveThreshold);
    }

    [Theory]
    [InlineData(WindowState.Normal, "□", "Maximize")]
    [InlineData(WindowState.Maximized, "❐", "Restore")]
    public void MaximizeRestoreButtonState_MatchesWindowState(
        WindowState windowState,
        string expectedGlyph,
        string expectedToolTip
    )
    {
        var glyph = InvokeStatic<string>("GetMaximizeRestoreGlyph", windowState);
        var toolTip = InvokeStatic<string>("GetMaximizeRestoreToolTip", windowState);

        Assert.Equal(expectedGlyph, glyph);
        Assert.Equal(expectedToolTip, toolTip);
    }

    [Fact]
    public void CalculateWindowControlStripWidth_ReservesSpaceForAllButtons()
    {
        var stripWidth = InvokeStatic<double>(
            "CalculateWindowControlStripWidth",
            MainWindow.WindowControlButtonWidth,
            3,
            MainWindow.WindowControlButtonSpacing.Left
        );

        Assert.Equal(136d, stripWidth, precision: 6);
    }

    [Fact]
    public void SpacingDictionary_DefinesSpaceTop4()
    {
        var dictionary = RunOnStaThread(
            () => (ResourceDictionary)Application.LoadComponent(
                new Uri("/CaseGraph.App;component/Themes/Spacing.xaml", UriKind.Relative)
            )
        );

        Assert.True(dictionary.Contains("SpaceTop4"));
    }

    private static T InvokeStatic<T>(string methodName, params object[] args)
    {
        var method = typeof(MainWindow).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static
        );

        Assert.NotNull(method);
        return (T)method!.Invoke(null, args)!;
    }

    private static T RunOnStaThread<T>(Func<T> action)
    {
        T? result = default;
        Exception? captured = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured is not null)
        {
            ExceptionDispatchInfo.Capture(captured).Throw();
        }

        return result!;
    }
}
