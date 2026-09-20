using System;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Kiosk
{
    /// <summary>
    /// A grid of equal columns that wraps.
    ///
    /// The platform's own wrap panel sizes each child to what it asks for, so
    /// a row of cards ends up ragged — and on a television a ragged row of
    /// cards reads as a mistake rather than as a layout. This one divides the
    /// width it is given into equal columns, which is what the design draws.
    /// </summary>
    public sealed class EvenWrapPanel : Panel
    {
        public int Columns { get; set; } = 4;
        public double Gap { get; set; } = 26;

        private double ColumnWidth(double available) =>
            Math.Max(0, (available - Gap * (Columns - 1)) / Columns);

        protected override Size MeasureOverride(Size available)
        {
            var width = ColumnWidth(available.Width);
            double rowHeight = 0, total = 0;
            var column = 0;

            foreach (var child in Children)
            {
                child.Measure(new Size(width, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
                if (++column < Columns) continue;

                total += rowHeight + Gap;
                rowHeight = 0;
                column = 0;
            }
            if (column > 0) total += rowHeight;
            else if (total > 0) total -= Gap;

            return new Size(available.Width, total);
        }

        protected override Size ArrangeOverride(Size final)
        {
            var width = ColumnWidth(final.Width);
            double y = 0, rowHeight = 0;
            var column = 0;

            foreach (var child in Children)
            {
                var x = column * (width + Gap);
                child.Arrange(new Rect(x, y, width, child.DesiredSize.Height));
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);

                if (++column < Columns) continue;
                y += rowHeight + Gap;
                rowHeight = 0;
                column = 0;
            }
            return final;
        }
    }
}
