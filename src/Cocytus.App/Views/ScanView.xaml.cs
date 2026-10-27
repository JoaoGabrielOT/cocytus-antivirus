using System.Windows;
using System.Windows.Controls;
using Cocytus.App.ViewModels;

namespace Cocytus.App.Views;

public partial class ScanView : UserControl
{
    public ScanView()
    {
        InitializeComponent();
    }

    private void OnTargetsDragOver(object sender, DragEventArgs e)
    {
        var running = DataContext is ScanViewModel { IsRunning: true };
        e.Effects = !running && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnTargetsDrop(object sender, DragEventArgs e)
    {
        if (DataContext is ScanViewModel { IsRunning: false } vm && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            vm.AddTargets(paths);
        }
    }

    private void OnResultsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is ScanViewModel vm)
        {
            vm.SelectedRows = ResultsGrid.SelectedItems.OfType<ResultRowViewModel>().ToList();
        }
    }
}
