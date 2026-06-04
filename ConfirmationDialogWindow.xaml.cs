using System.Windows;
using System.Windows.Input;

namespace ANEVRED;

public partial class ConfirmationDialogWindow : Window
{
    public ConfirmationDialogWindow(string title, string message, string confirmText, string cancelText)
    {
        InitializeComponent();
        DialogTitle = title;
        Message = message;
        ConfirmText = confirmText;
        CancelText = cancelText;
    }

    public string DialogTitle { get; }

    public string Message { get; }

    public string ConfirmText { get; }

    public string CancelText { get; }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        CancelButton.Focus();
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    private void ConfirmClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
