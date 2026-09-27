using System.Windows;
using Xunit;

namespace Avalanche.Tests
{
    public class FlyoutPlacementTests
    {
        [Fact]
        public void LeftRailUsesBottomLeftContentCorner()
        {
            var placement = FlyoutPlacement.PaneCorner(
                new Size(244, 460), new Size(1200, 700), alignRight: false)[0];

            Assert.Equal(new Point(-16, 260), placement.Point);
        }

        [Fact]
        public void RightRailMirrorsFlyoutToBottomRightContentCorner()
        {
            var placement = FlyoutPlacement.PaneCorner(
                new Size(244, 460), new Size(1200, 700), alignRight: true)[0];

            Assert.Equal(new Point(972, 260), placement.Point);
        }

        [Fact]
        public void TallFlyoutStaysBelowToolbarOnEitherSide()
        {
            var left = FlyoutPlacement.PaneCorner(
                new Size(244, 800), new Size(1200, 700), alignRight: false)[0];
            var right = FlyoutPlacement.PaneCorner(
                new Size(244, 800), new Size(1200, 700), alignRight: true)[0];

            Assert.Equal(0, left.Point.Y);
            Assert.Equal(0, right.Point.Y);
        }
    }
}
