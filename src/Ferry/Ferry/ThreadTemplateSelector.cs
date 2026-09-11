using System.Windows;
using System.Windows.Controls;

namespace Ferry;

public sealed class ThreadTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TextTemplate { get; set; }
    public DataTemplate? FileTemplate { get; set; }
    public DataTemplate? SeparatorTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is ThreadSeparator) return SeparatorTemplate;
        return item is FerryMessage m && !m.IsText ? FileTemplate : TextTemplate;
    }
}
