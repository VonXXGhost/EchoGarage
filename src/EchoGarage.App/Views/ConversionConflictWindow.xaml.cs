using System.Windows;
using System.Windows.Input;
using EchoGarage.App.Services;

namespace EchoGarage.App.Views;

/// <summary>Batch-level conflict prompt shown before a conversion starts.</summary>
public partial class ConversionConflictWindow : ThemedWindow
{
    public ConversionConflictWindow(int conflictCount)
    {
        InitializeComponent();
        MessageText.Text = $"有 {conflictCount} 个目标文件已经存在。是否覆盖这些文件？";
    }

    public ConversionConflictChoice Choice { get; private set; } = ConversionConflictChoice.Cancel;

    private void OnOverwriteClick(object sender, RoutedEventArgs e) => Finish(ConversionConflictChoice.OverwriteAll);

    private void OnSkipClick(object sender, RoutedEventArgs e) => Finish(ConversionConflictChoice.SkipConflicts);

    private void OnRenameClick(object sender, RoutedEventArgs e) => Finish(ConversionConflictChoice.RenameAll);

    private void OnCancelClick(object sender, RoutedEventArgs e) => Finish(ConversionConflictChoice.Cancel);

    private void Finish(ConversionConflictChoice choice)
    {
        Choice = choice;
        DialogResult = choice != ConversionConflictChoice.Cancel;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Choice = ConversionConflictChoice.Cancel;
            DialogResult = false;
        }
    }
}
