using System.Windows;
using System.Windows.Input;
using CryoTray.Services;

namespace CryoTray.Views
{
    public partial class ConfirmExitWindow : Window
    {
        public ConfirmExitWindow()
        {
            InitializeComponent();
            LocalizationService.Instance.ApplyWindow(this);
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void BtnYes_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void BtnNo_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
