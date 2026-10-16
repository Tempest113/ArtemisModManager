using System.Windows.Markup;
using Artemis.Core.Text;

namespace Artemis.App;

/// <summary>XAML text from the loc file: <c>Content="{local:T ui.dismiss}"</c>.</summary>
public sealed class TExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(key);
}
