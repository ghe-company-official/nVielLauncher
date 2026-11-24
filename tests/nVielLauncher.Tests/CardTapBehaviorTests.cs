using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using nVielLauncher.Behaviors;
using Xunit;

namespace nVielLauncher.Tests;

public class CardTapBehaviorTests
{
    [Fact]
    public void DepressionAdornerBuildsItsVisualTreeWithoutThrowing()
    {
        Exception? failure = null;
        var built = false;
        var hitTestVisible = true;
        var childCount = 0;
        var clipRect = Rect.Empty;

        // WPF objects are thread-affine, so build and inspect them on one STA thread.
        var thread = new Thread(() =>
        {
            try
            {
                var card = new Border
                {
                    Width = 240,
                    Height = 120,
                    CornerRadius = new CornerRadius(12)
                };
                card.Measure(new Size(240, 120));
                card.Arrange(new Rect(0, 0, 240, 120));

                var adornerType = typeof(CardTapAnimationBehavior)
                    .GetNestedType("DepressionAdorner", BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("DepressionAdorner type was not found.");

                var ctor = adornerType.GetConstructor(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    binder: null,
                    types: new[] { typeof(UIElement), typeof(Point) },
                    modifiers: null)
                    ?? throw new InvalidOperationException("DepressionAdorner constructor was not found.");

                var adorner = (Adorner)ctor.Invoke(new object[] { card, new Point(60, 40) });

                built = true;
                hitTestVisible = adorner.IsHitTestVisible;
                childCount = VisualTreeHelper.GetChildrenCount(adorner);
                clipRect = ((RectangleGeometry)adorner.Clip).Rect;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Adorner construction timed out.");
        Assert.Null(failure);
        Assert.True(built);
        Assert.False(hitTestVisible);

        // The dent lives in a single visual child that is clipped to the card.
        Assert.Equal(1, childCount);
        Assert.Equal(new Rect(0, 0, 240, 120), clipRect);
    }
}
