using System.Windows;
using Microsoft.Win32;
using PremiereAutoDialogueXml.App.ViewModels;

namespace PremiereAutoDialogueXml.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel = new();

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

    private void InspectSelection_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.InspectSelection();
    }
}
