using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace Ferry.Tests;

public class ImageWindowInteractionTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData("PrevButton")]
    [InlineData("NextButton")]
    public void NavigationButton_HasHitTestableAncestorChain(string buttonName)
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ImageWindow.xaml");
        var document = XDocument.Load(fixture);
        var button = document
            .Descendants(Presentation + "Button")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == buttonName);

        var blockingAncestor = button
            .Ancestors()
            .FirstOrDefault(element =>
                string.Equals((string?)element.Attribute("IsHitTestVisible"), "False", StringComparison.OrdinalIgnoreCase));

        Assert.Null(blockingAncestor);
    }
}
