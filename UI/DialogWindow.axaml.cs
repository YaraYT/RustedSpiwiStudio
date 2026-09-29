namespace RustedShpizhionStudio.UI;

public partial class DialogWindow : Window
{
    private readonly TaskCompletionSource<bool> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public DialogWindow(string message,string yes="OK",string? no=null)
    {
        InitializeComponent();
        UiAnimationService.PrepareWindow(this);
        Message.Text=message;
        YesButton.Content=yes;
        if(no is null)NoButton.IsVisible=false;
        else{NoButton.IsVisible=true;NoButton.Content=no;}
    }
    private void Yes_Click(object? s,RoutedEventArgs e){_tcs.TrySetResult(true);Close(true);}
    private void No_Click(object? s,RoutedEventArgs e){_tcs.TrySetResult(false);Close(false);}
    public static async Task<bool> AskAsync(Window owner,string message,string yes,string no){var d=new DialogWindow(message,yes,no);await d.ShowDialog(owner);return await d._tcs.Task;}
    public static async Task ShowAsync(Window owner,string message,string ok="OK"){var d=new DialogWindow(message,ok);await d.ShowDialog(owner);}
}
