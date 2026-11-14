using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace BiliDownloader.Desktop;

public static class MessageBox
{
    public enum MessageBoxButtons { OK, YesNo, YesNoCancel }
    public enum MessageBoxResult { OK, Yes, No, Cancel }

    public static async Task<MessageBoxResult> Show(Window owner, string text, string title, MessageBoxButtons buttons = MessageBoxButtons.OK)
    {
        var win = new Window
        {
            Title = title,
            Width = 400,
            MinHeight = 120,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            CanResize = false,
            Background = (IBrush?)Application.Current?.FindResource("WindowBg")
        };

        var result = MessageBoxResult.Cancel;
        var sp = new StackPanel { Margin = new Thickness(20) };
        sp.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13 });

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        Button MakeBtn(string content, MessageBoxResult r)
        {
            var btn = new Button { Content = content, Padding = new Thickness(16, 6), Margin = new Thickness(4, 0, 0, 0), MinWidth = 70 };
            btn.Classes.Add("primary");
            btn.Click += (_, _) => { result = r; win.Close(); };
            return btn;
        }

        if (buttons == MessageBoxButtons.OK)
        {
            btnPanel.Children.Add(MakeBtn("确定", MessageBoxResult.OK));
        }
        else if (buttons == MessageBoxButtons.YesNo)
        {
            btnPanel.Children.Add(MakeBtn("否", MessageBoxResult.No));
            var yesBtn = MakeBtn("是", MessageBoxResult.Yes);
            yesBtn.Classes.Clear(); yesBtn.Classes.Add("primary");
            btnPanel.Children.Insert(0, yesBtn);
        }
        else
        {
            btnPanel.Children.Add(MakeBtn("取消", MessageBoxResult.Cancel));
            btnPanel.Children.Add(MakeBtn("否", MessageBoxResult.No));
            var yesBtn = MakeBtn("是", MessageBoxResult.Yes);
            yesBtn.Classes.Clear(); yesBtn.Classes.Add("primary");
            btnPanel.Children.Insert(0, yesBtn);
        }

        sp.Children.Add(btnPanel);
        win.Content = sp;
        await win.ShowDialog(owner);
        return result;
    }
}
