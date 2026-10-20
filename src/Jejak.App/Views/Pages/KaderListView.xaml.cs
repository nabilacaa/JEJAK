using System.Windows.Controls;
using System.Windows.Input;
using Jejak.App.ViewModels;

namespace Jejak.App.Views.Pages;

public partial class KaderListView : UserControl
{
    public KaderListView()
    {
        InitializeComponent();
    }

    // Klik ganda pada baris membuka detail kader.
    private void Tabel_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is KaderListViewModel vm && Tabel.SelectedItem is KaderBaris baris
            && e.OriginalSource is System.Windows.DependencyObject asal && ItemsControl.ContainerFromElement(Tabel, asal) is DataGridRow)
            vm.LihatCommand.Execute(baris);
    }
}
