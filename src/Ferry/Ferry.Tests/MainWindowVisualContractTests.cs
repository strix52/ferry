using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Xml.Linq;
using Xunit;

namespace Ferry.Tests;

public class MainWindowVisualContractTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void MainShell_DisablesTranslucentMicaBackdrop()
    {
        Assert.False(MainWindow.UseMicaBackdrop);
    }

    [Fact]
    public void EmptyComposerPadding_FitsWithinCircularControls()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Controls.xaml");
        var document = XDocument.Load(fixture);
        var style = document
            .Descendants(Presentation + "Style")
            .Single(element => (string?)element.Attribute(Xaml + "Key") == "ComposerTextBox");
        var paddingSetter = style
            .Elements(Presentation + "Setter")
            .Single(element => (string?)element.Attribute("Property") == "Padding");
        var padding = (Thickness)new ThicknessConverter().ConvertFromInvariantString(
            (string?)paddingSetter.Attribute("Value") ?? throw new InvalidOperationException("Composer padding is missing"))!;

        Assert.True(
            padding.Top + padding.Bottom <= 8,
            $"Composer vertical padding {padding.Top + padding.Bottom} makes the empty editor taller than its 44-DIP controls.");
    }
}
