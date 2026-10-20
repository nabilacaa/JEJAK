using System.Windows.Controls;
using System.Windows.Input;
using Jejak.App.ViewModels;

namespace Jejak.App.Views.Pages;

public partial class RisikoView : UserControl
{
    public RisikoView()
    {
        InitializeComponent();
    }

    private void Tabel_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is RisikoViewModel vm && Tabel.SelectedItem is RisikoBaris baris
            && e.OriginalSource is System.Windows.DependencyObject asal && ItemsControl.ContainerFromElement(Tabel, asal) is DataGridRow)
            vm.LihatCommand.Execute(baris);
    }
}
