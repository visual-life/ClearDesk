using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace ClearDesk
{
    /// <summary>
    /// 2.2.3: sharp text in scaled widgets. A widget that is larger than its design size is drawn through a LayoutTransform; text laid out at the design size and
    /// then enlarged by 1.15× lands on fractional pixels and loses its hinting — stems come out two pixels wide and grey. Here every TextBlock of the widget is laid out
    /// at the final size instead (font size × s, GDI-compatible "Display" metrics) and shrunk by 1/s inside the transform: the two scales cancel for the glyphs, so they
    /// are rasterised at their real size and hinted, while the space the text takes in the layout is unchanged.
    /// </summary>
    internal static class CrispText
    {
        static readonly DependencyProperty BaseSize = DependencyProperty.RegisterAttached("CrispBaseSize", typeof(double), typeof(CrispText), new PropertyMetadata(0.0));
        static readonly DependencyProperty AppliedSize = DependencyProperty.RegisterAttached("CrispAppliedSize", typeof(double), typeof(CrispText), new PropertyMetadata(0.0));
        /// <summary>Off by default: the screen measurements of 2.2.3 showed no net gain across widgets (see docs/releases/2.2.3.md). CLEARDESK_CRISPTEXT=1 turns the experiment on.</summary>
        internal static bool Enabled = Environment.GetEnvironmentVariable("CLEARDESK_CRISPTEXT") == "1";
        internal static bool TestDisable;                  // tests: reproduce the 2.2.2 drawing

        /// <summary>True when the scale is large enough to need the counter-scaling.</summary>
        internal static bool Needed(double scale) { return Math.Abs(scale - 1) >= .02 && scale > .2 && scale < 10; }
        /// <summary>The font size that is laid out for a design size at a content scale.</summary>
        internal static double FontFor(double design, double scale) { return Math.Max(1, Math.Round(design * scale)); }

        /// <summary>Applies (or, at scale 1, removes) the counter-scaling for every TextBlock in the logical tree under <paramref name="root"/>. Returns how many were changed.</summary>
        internal static int Apply(DependencyObject root, double scale)
        {
            int changed = 0; bool on = Enabled && Needed(scale) && !TestDisable;
            var list = new List<TextBlock>(); Collect(root, list);
            foreach (var tb in list) if (Update(tb, scale, on)) changed++;
            return changed;
        }

        static void Collect(DependencyObject o, List<TextBlock> list)
        {
            var tb = o as TextBlock; if (tb != null) list.Add(tb);
            foreach (object child in LogicalTreeHelper.GetChildren(o)) { var d = child as DependencyObject; if (d != null) Collect(d, list); }
        }

        static bool Update(TextBlock tb, double s, bool on)
        {
            double baseSize = (double)tb.GetValue(BaseSize), applied = (double)tb.GetValue(AppliedSize), fs = tb.FontSize;
            bool ours = baseSize > 0;
            if (ours && Math.Abs(fs - applied) > .001) { baseSize = fs; }                       // the widget set a new (design) size itself
            else if (!ours) baseSize = fs;
            if (!ours && !on) return false;
            var current = tb.LayoutTransform as ScaleTransform;
            if (!ours && tb.LayoutTransform != null && tb.LayoutTransform != Transform.Identity) return false;      // somebody else's transform
            if (BindingOperations.IsDataBound(tb, TextBlock.FontSizeProperty)) return false;
            if (on)
            {
                double want = FontFor(baseSize, s), inv = 1 / s;
                if (ours && Math.Abs(fs - want) < .001 && current != null && Math.Abs(current.ScaleX - inv) < 1e-9) return false;
                tb.SetValue(BaseSize, baseSize); tb.SetValue(AppliedSize, want);
                tb.FontSize = want; tb.LayoutTransform = new ScaleTransform(inv, inv);
                TextOptions.SetTextFormattingMode(tb, TextFormattingMode.Display);
                return true;
            }
            // back to the design size
            tb.FontSize = baseSize; tb.LayoutTransform = Transform.Identity; tb.ClearValue(TextOptions.TextFormattingModeProperty);
            tb.ClearValue(BaseSize); tb.ClearValue(AppliedSize);
            return true;
        }

        internal static double Base(TextBlock tb) { double b = (double)tb.GetValue(BaseSize); return b > 0 ? b : tb.FontSize; }
    }
}
