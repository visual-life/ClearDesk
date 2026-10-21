using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;

namespace ClearDesk
{
    internal static class WidgetDrag
    {
        internal static bool IsInteractive(DependencyObject node, DependencyObject root)
        {
            while(node != null && node != root)
            {
                if(node is ButtonBase || node is TextBoxBase || node is PasswordBox || node is Selector || node is Slider || node is Thumb || node is ScrollBar || node is MenuItem || node is Hyperlink) return true;
                var content=node as ContentElement;
                node=content!=null ? ContentOperations.GetParent(content) ?? (content as FrameworkContentElement)?.Parent : VisualTreeHelper.GetParent(node);
            }
            return false;
        }
    }
}
