using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using PremiereAutoDialogueXml.App.ViewModels;

namespace PremiereAutoDialogueXml.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel = new();
    private bool _closeAfterCancellation;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    private void SelectXml_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Chọn XML do Premiere xuất",
            Filter = "Final Cut Pro XML (*.xml)|*.xml|Tất cả tệp (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.SetXmlPath(dialog.FileName);
        }
    }

    private void SelectOutputDirectory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Chọn thư mục lưu kết quả",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.SetOutputDirectory(dialog.FolderName);
        }
    }

    private async void InspectSelection_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.InspectSelectionAsync();
    }

    private async void StartProcessing_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.StartProcessingAsync();
    }

    private void CancelProcessing_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.RequestCancellation();
    }

    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        var path = _viewModel.OutputXmlPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            MessageBox.Show(
                this,
                "Không còn tìm thấy XML kết quả ở vị trí đã xuất.",
                "Không thể mở kết quả",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{path}\"",
            UseShellExecute = true
        });
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeAfterCancellation || !_viewModel.IsBusy)
        {
            return;
        }

        e.Cancel = true;
        IsEnabled = false;
        await _viewModel.CancelAndWaitAsync();
        _closeAfterCancellation = true;
        Close();
    }
}
